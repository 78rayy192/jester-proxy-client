using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Jester.Host.Models;

namespace Jester.Host.Services;

public sealed class CoreManager : IDisposable
{
    readonly SettingsStore _settings;
    readonly ProfileStore _profiles;
    readonly CoreDownloader _downloader;
    readonly object _gate = new();
    Process? _process;
    string _coreVersion = "";
    static readonly ConcurrentQueue<string> LogBuffer = new();
    static readonly object LogGate = new();
    static System.Threading.Timer? LogTimer;
    public string ApiSecret { get; private set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public CoreDownloader Downloader => _downloader;
    public bool Running { get { lock (_gate) return _process is { HasExited: false }; } }
    public int Pid { get { lock (_gate) return Running ? _process!.Id : 0; } }

    public CoreManager(SettingsStore settings, ProfileStore profiles, CoreDownloader downloader)
    {
        _settings = settings;
        _profiles = profiles;
        _downloader = downloader;
    }

    public CoreStatus Snapshot()
    {
        var s = _settings.Current;
        var installed = File.Exists(AppPaths.MihomoExe);
        var admin = AppPaths.IsAdministrator();
        return new CoreStatus
        {
            Running = Running,
            CoreInstalled = installed,
            WintunInstalled = File.Exists(AppPaths.WintunDll),
            IsAdmin = admin,
            NeedsAdmin = s.TunEnabled && !admin,
            Pid = Pid,
            Version = ReadCoreVersion(),
            DownloadProgress = _downloader.Progress,
            ActiveProfileId = s.ActiveProfileId,
            Mode = s.Mode,
            Message = _downloader.Busy ? _downloader.Stage : (Running ? "online" : "idle")
        };
    }

    public async Task<CoreStatus> StartAsync(CancellationToken ct = default)
    {
        var s = _settings.Current;
        if (!File.Exists(AppPaths.MihomoExe))
            throw new InvalidOperationException("Ядро mihomo не установлено.");
        if (string.IsNullOrWhiteSpace(s.ActiveProfileId))
            throw new InvalidOperationException("Сначала выберите профиль.");
        if (s.TunEnabled && !AppPaths.IsAdministrator())
            return Snapshot() with { Message = "admin-required", NeedsAdmin = true };

        var profile = _profiles.Get(s.ActiveProfileId)
            ?? throw new InvalidOperationException("Активный профиль не найден.");
        var yaml = await File.ReadAllTextAsync(_profiles.ConfigPath(profile.Id), ct);

        try { await RuleListService.EnsureAsync(s, ct); }
        catch (Exception ex) { AppendLog("rule-list: " + ex.Message); }

        var merged = ConfigBuilder.Merge(yaml, s, ApiSecret);
        AppPaths.Ensure();
        await File.WriteAllTextAsync(AppPaths.RuntimeConfig, merged, ct);

        Stop();
        CopyWintunBesideCore();

        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.MihomoExe,
            WorkingDirectory = AppPaths.Run,
            Arguments = $"-d \"{AppPaths.Run}\" -f \"{AppPaths.RuntimeConfig}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["DISABLE_LOOPBACK_DETECTOR"] = "1";

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
        if (!proc.Start()) throw new InvalidOperationException("Не удалось запустить ядро.");
        try
        {
            proc.PriorityClass = s.HighPriority ? ProcessPriorityClass.High : ProcessPriorityClass.Normal;
        }
        catch { /* no rights */ }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        EnsureLogFlusher();
        lock (_gate) _process = proc;

        await WaitReady(s.ExternalPort, ct);

        // TUN already captures all traffic — system proxy on top = double hop / slow.
        if (s.SystemProxy && !s.TunEnabled)
            SystemProxy.Set(true, s.MixedPort);
        else
            SystemProxy.Set(false, 0);

        return Snapshot();
    }

    public void Stop()
    {
        Process? proc;
        lock (_gate)
        {
            proc = _process;
            _process = null;
        }
        if (proc == null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(true);
                proc.WaitForExit(4000);
            }
        }
        catch { /* ignore */ }
        finally { proc.Dispose(); }

        FlushLog();
        try { SystemProxy.Set(false, 0); } catch { /* ignore */ }
        if (_settings.Current.SilentLogs || _settings.Current.StealthMode)
        {
            try
            {
                if (File.Exists(AppPaths.CoreLog))
                    File.WriteAllText(AppPaths.CoreLog, "");
            }
            catch { /* ignore */ }
        }
    }

    public async Task<LeakCheckResult> LeakCheckAsync(CancellationToken ct = default)
    {
        var result = new LeakCheckResult { Running = Running };
        if (!Running)
        {
            result.Message = "Сначала запустите ядро";
            return result;
        }

        var port = _settings.Current.MixedPort;
        try
        {
            using var direct = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            result.RealIp = (await direct.GetStringAsync("https://api.ipify.org", ct)).Trim();
        }
        catch (Exception ex)
        {
            result.RealIp = null;
            result.Message = "Не удалось получить реальный IP: " + ex.Message;
        }

        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true
            };
            using var via = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            result.ExitIp = (await via.GetStringAsync("https://api.ipify.org", ct)).Trim();
        }
        catch (Exception ex)
        {
            result.ExitIp = null;
            if (string.IsNullOrEmpty(result.Message))
                result.Message = "Не удалось получить IP выхода: " + ex.Message;
        }

        if (!string.IsNullOrEmpty(result.RealIp) && !string.IsNullOrEmpty(result.ExitIp))
        {
            result.Masked = !string.Equals(result.RealIp, result.ExitIp, StringComparison.OrdinalIgnoreCase);
            result.Message = result.Masked
                ? "Маска работает: выход ≠ реальный IP"
                : "Внимание: выход совпал с реальным IP (для split-режима часть сайтов идёт напрямую — это нормально, если не TUN global)";
        }

        return result;
    }

    public async Task<CoreStatus> RestartAsync(CancellationToken ct = default)
    {
        Stop();
        await Task.Delay(250, ct);
        return await StartAsync(ct);
    }

    public void Elevate()
    {
        if (AppPaths.IsAdministrator() || Environment.ProcessPath is null) return;
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        Process.Start(psi);
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
            System.Windows.Application.Current.Shutdown());
    }

    async Task WaitReady(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var url = $"http://127.0.0.1:{port}/version";
        for (var i = 0; i < 25; i++)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_process is { HasExited: true })
                    throw new InvalidOperationException("Ядро завершилось при старте. Смотрите журнал.");
            }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiSecret);
                using var res = await http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return;
            }
            catch { /* retry */ }
            await Task.Delay(200, ct);
        }
        throw new InvalidOperationException("Ядро не ответило на Clash API.");
    }

    static void CopyWintunBesideCore()
    {
        if (!File.Exists(AppPaths.WintunDll)) return;
        var dest = Path.Combine(AppPaths.Run, "wintun.dll");
        File.Copy(AppPaths.WintunDll, dest, true);
        var dest2 = Path.Combine(Path.GetDirectoryName(AppPaths.MihomoExe)!, "wintun.dll");
        if (!File.Exists(dest2)) File.Copy(AppPaths.WintunDll, dest2, true);
    }

    string ReadCoreVersion()
    {
        if (!string.IsNullOrEmpty(_coreVersion)) return _coreVersion;
        if (!File.Exists(AppPaths.MihomoExe)) return "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.MihomoExe,
                Arguments = "-v",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (p == null) return "";
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            _coreVersion = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }
        catch { _coreVersion = ""; }
        return _coreVersion;
    }

    public async Task<Dictionary<string, int>> TestDelaysAsync(string url, int timeoutMs, CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!Running) return result;

        var port = _settings.Current.ExternalPort;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + ApiSecret);

        // Prefer group delay on AUTO / PROXY
        foreach (var group in new[] { "AUTO", "PROXY" })
        {
            try
            {
                var u = $"http://127.0.0.1:{port}/group/{Uri.EscapeDataString(group)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";
                using var res = await http.GetAsync(u, ct);
                if (!res.IsSuccessStatusCode) continue;
                var json = await res.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                        result[prop.Name] = prop.Value.GetInt32();
                }
                if (result.Count > 0) return result;
            }
            catch { /* try next / per-node */ }
        }

        // Fallback: list leaf proxies and test in batches
        try
        {
            using var listRes = await http.GetAsync($"http://127.0.0.1:{port}/proxies", ct);
            listRes.EnsureSuccessStatusCode();
            using var listDoc = System.Text.Json.JsonDocument.Parse(await listRes.Content.ReadAsStringAsync(ct));
            var names = new List<string>();
            if (listDoc.RootElement.TryGetProperty("proxies", out var proxies))
            {
                foreach (var p in proxies.EnumerateObject())
                {
                    var type = p.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (type is "Selector" or "URLTest" or "Fallback" or "LoadBalance" or "Relay" or "Direct" or "Reject" or "Pass")
                        continue;
                    if (p.Name is "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS" or "COMPATIBLE" or "GLOBAL")
                        continue;
                    names.Add(p.Name);
                }
            }

            foreach (var chunk in names.Chunk(6))
            {
                var tasks = chunk.Select(async name =>
                {
                    try
                    {
                        var u = $"http://127.0.0.1:{port}/proxies/{Uri.EscapeDataString(name)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";
                        using var res = await http.GetAsync(u, ct);
                        var body = await res.Content.ReadAsStringAsync(ct);
                        using var d = System.Text.Json.JsonDocument.Parse(body);
                        if (d.RootElement.TryGetProperty("delay", out var delay))
                            return (name, delay.GetInt32());
                    }
                    catch { /* dead */ }
                    return (name, 0);
                });
                foreach (var (name, delay) in await Task.WhenAll(tasks))
                    result[name] = delay;
            }
        }
        catch { /* ignore */ }

        return result;
    }

    static void EnsureLogFlusher()
    {
        LogTimer ??= new System.Threading.Timer(_ => FlushLog(), null, 400, 400);
    }

    static void AppendLog(string line)
    {
        LogBuffer.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
        if (LogBuffer.Count > 80) FlushLog();
    }

    static void FlushLog()
    {
        if (LogBuffer.IsEmpty) return;
        lock (LogGate)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                while (LogBuffer.TryDequeue(out var line))
                    sb.AppendLine(line);
                if (sb.Length > 0) File.AppendAllText(AppPaths.CoreLog, sb.ToString());
            }
            catch { /* ignore */ }
        }
    }

    public static string ReadLogTail(int lines = 250)
    {
        if (!File.Exists(AppPaths.CoreLog)) return "";
        using var fs = new FileStream(AppPaths.CoreLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var all = reader.ReadToEnd().Split('\n');
        return string.Join('\n', all.TakeLast(lines));
    }

    public void Dispose() => Stop();
}
