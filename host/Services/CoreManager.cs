using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Jester.Host.Models;

namespace Jester.Host.Services;

public sealed class CoreManager : IDisposable
{
    readonly SettingsStore _settings;
    readonly ProfileStore _profiles;
    readonly CoreDownloader _downloader;
    readonly object _gate = new();
    readonly SemaphoreSlim _lifecycle = new(1, 1);
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
        await _lifecycle.WaitAsync(ct);
        try
        {
            var s = _settings.Current;
            if (!File.Exists(AppPaths.MihomoExe))
                throw new InvalidOperationException("Ядро не установлено.");
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

            StopUnlocked();
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
            proc.Exited += (_, _) => OnCoreExited(proc);
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

            try
            {
                await WaitReady(s.ExternalPort, ct);
            }
            catch
            {
                StopUnlocked();
                throw;
            }

            // TUN already captures all traffic — system proxy on top = double hop / slow.
            if (s.SystemProxy && !s.TunEnabled)
                SystemProxy.Set(true, s.MixedPort);
            else
                SystemProxy.Set(false, 0);

            return Snapshot();
        }
        finally { _lifecycle.Release(); }
    }

    void OnCoreExited(Process proc)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_process, proc)) return;
            _process = null;
        }
        try { SystemProxy.Set(false, 0); } catch { /* ignore */ }
        FlushLog();
        AppendLog("core exited unexpectedly");
    }

    public void Stop()
    {
        if (!_lifecycle.Wait(8000)) return;
        try { StopUnlocked(); }
        finally { _lifecycle.Release(); }
    }

    void StopUnlocked()
    {
        Process? proc;
        lock (_gate)
        {
            proc = _process;
            _process = null;
        }
        if (proc == null)
        {
            try { SystemProxy.Set(false, 0); } catch { /* ignore */ }
            return;
        }
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(true);
                proc.WaitForExit(4000);
            }
        }
        catch { /* ignore */ }
        finally { try { proc.Dispose(); } catch { /* ignore */ } }

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
        var s = _settings.Current;
        var result = new LeakCheckResult
        {
            Running = Running,
            TunMode = s.TunEnabled,
            Checks = BuildShieldChecks(s)
        };

        if (!Running)
        {
            result.Message = "Сначала запустите ядро.";
            result.Score = ScoreFromChecks(result.Checks, masked: null, running: false);
            return result;
        }

        var port = s.MixedPort;
        if (!s.TunEnabled)
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
                using var direct = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                result.RealIp = (await direct.GetStringAsync("https://api.ipify.org", ct)).Trim();
            }
            catch (Exception)
            {
                result.RealIp = null;
                result.Message = "Не удалось определить внешний адрес.";
            }
        }
        else
        {
            result.RealIp = null;
            result.Message = "Туннель включён. Сравнивается адрес выхода и состояние защиты.";
        }

        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true
            };
            using var via = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            result.ExitIp = (await via.GetStringAsync("https://api.ipify.org", ct)).Trim();
        }
        catch (Exception)
        {
            result.ExitIp = null;
            if (string.IsNullOrEmpty(result.Message) || result.Message.StartsWith("Туннель", StringComparison.Ordinal))
                result.Message = "Не удалось определить адрес выхода.";
        }

        bool? maskedSignal = null;
        if (!s.TunEnabled && !string.IsNullOrEmpty(result.RealIp) && !string.IsNullOrEmpty(result.ExitIp))
        {
            result.Masked = !string.Equals(result.RealIp, result.ExitIp, StringComparison.OrdinalIgnoreCase);
            maskedSignal = result.Masked;
            result.Message = result.Masked
                ? "Адрес выхода отличается от внешнего адреса."
                : "Адрес выхода совпал с внешним адресом. Для полного перенаправления включите туннель.";
        }
        else if (s.TunEnabled && !string.IsNullOrEmpty(result.ExitIp))
        {
            result.Masked = true;
            maskedSignal = true;
            if (string.IsNullOrEmpty(result.Message) || result.Message.StartsWith("TUN", StringComparison.Ordinal))
                result.Message = "Туннель активен. Адрес выхода получен.";
        }

        // Local UDP probes: closed = good for anti-QUIC / anti-STUN posture (best-effort).
        result.Checks.Add(await ProbeUdpBlockedAsync("Блокировка QUIC", 443, s.BlockQuic, ct));
        result.Checks.Add(await ProbeUdpBlockedAsync("Блокировка STUN", 3478, s.BlockStun, ct));

        result.Score = ScoreFromChecks(result.Checks, maskedSignal, Running);
        if (result.Score >= 85 && string.IsNullOrEmpty(result.Message))
            result.Message = "Параметры защиты в норме.";
        else if (result.Score < 55 && string.IsNullOrEmpty(result.Message))
            result.Message = "Защита недостаточна. Включите режим защиты или туннель.";

        return result;
    }

    static List<GhostCheck> BuildShieldChecks(AppSettings s) =>
    [
        new() { Id = "stealth", Label = "Режим защиты", Ok = s.StealthMode, Weight = 12 },
        new() { Id = "quic", Label = "Блокировка QUIC", Ok = s.BlockQuic, Weight = 10 },
        new() { Id = "stun", Label = "Блокировка STUN", Ok = s.BlockStun, Weight = 10 },
        new() { Id = "dns", Label = "DNS через туннель", Ok = s.DnsViaProxy, Weight = 12 },
        new() { Id = "dns-rules", Label = "DNS по правилам", Ok = s.DnsRespectRules, Weight = 6 },
        new() { Id = "tls", Label = "Случайный TLS", Ok = s.RandomFingerprint, Weight = 8 },
        new() { Id = "lan", Label = "Локальная сеть закрыта", Ok = !s.AllowLan, Weight = 8 },
        new() { Id = "ipv6", Label = "IPv6 отключён", Ok = !s.Ipv6, Weight = 6 },
        new() { Id = "logs", Label = "Журнал отключён", Ok = s.SilentLogs, Weight = 5 },
        new() { Id = "capture", Label = "Захват экрана отключён", Ok = s.HideFromCapture || s.StealthMode, Weight = 8 },
        new() { Id = "mrs", Label = "Бинарные правила", Ok = s.UseMrsRules, Weight = 5 },
        new() { Id = "tun", Label = "Системный туннель", Ok = s.TunEnabled, Weight = 10 },
    ];

    static int ScoreFromChecks(List<GhostCheck> checks, bool? masked, bool running)
    {
        var weight = checks.Sum(c => c.Weight);
        var got = checks.Where(c => c.Ok).Sum(c => c.Weight);
        var score = weight == 0 ? 0 : (int)Math.Round(100.0 * got / weight);
        if (running && masked == true) score = Math.Min(100, score + 8);
        if (running && masked == false) score = Math.Max(0, score - 25);
        if (!running) score = Math.Max(0, score - 20);
        return Math.Clamp(score, 0, 100);
    }

    static async Task<GhostCheck> ProbeUdpBlockedAsync(string label, int port, bool expectedBlocked, CancellationToken ct)
    {
        if (!expectedBlocked)
            return new GhostCheck { Id = "udp-" + port, Label = label, Ok = false, Weight = 6, Detail = "Отключено в параметрах" };

        try
        {
            using var udp = new UdpClient();
            udp.Client.SendTimeout = 400;
            udp.Client.ReceiveTimeout = 400;
            await udp.SendAsync(new byte[] { 0 }, 1, "1.1.1.1", port);
            var receive = udp.ReceiveAsync();
            var done = await Task.WhenAny(receive, Task.Delay(350, ct));
            var answered = done == receive && !receive.IsFaulted;
            // If something answers quickly, block may be ineffective; no answer is OK/inconclusive → treat as ok.
            return new GhostCheck
            {
                Id = "udp-" + port,
                Label = label,
                Ok = !answered,
                Weight = 6,
                Detail = answered ? "Получен внешний ответ" : "Ответа нет"
            };
        }
        catch
        {
            return new GhostCheck { Id = "udp-" + port, Label = label, Ok = true, Weight = 6, Detail = "Порт недоступен" };
        }
    }

    /// <summary>Vanish: stop core, wipe logs, clear proxy, hide from capture, tray.</summary>
    public async Task<GhostPanicResult> PhantomPanicAsync(CancellationToken ct = default)
    {
        _settings.Patch(s =>
        {
            s.HideFromCapture = true;
            s.CloseToTray = true;
            s.SystemProxy = false;
            if (s.StealthMode)
            {
                s.SilentLogs = true;
            }
        });
        WindowActions.ApplyCapture?.Invoke();
        Stop();
        try
        {
            if (File.Exists(AppPaths.CoreLog))
                await File.WriteAllTextAsync(AppPaths.CoreLog, "", ct);
            if (File.Exists(AppPaths.RuntimeConfig))
                File.Delete(AppPaths.RuntimeConfig);
        }
        catch { /* ignore */ }

        WindowActions.CloseOrTray?.Invoke(true);
        return new GhostPanicResult
        {
            Ok = true,
            Message = "Подключение остановлено. Системный прокси сброшен. Окно свёрнуто в область уведомлений."
        };
    }

    /// <summary>Enable РФ Rush, restart if needed, select RUSH lane and pick fastest node.</summary>
    public async Task<RushResult> ActivateRushAsync(CancellationToken ct = default)
    {
        _settings.Patch(s =>
        {
            s.RfRushMode = true;
            s.PreferRushLane = true;
            s.TcpConcurrent = true;
            s.UnifiedDelay = true;
            if (!s.StealthMode)
            {
                s.BlockQuic = true;
                s.DnsViaProxy = true;
            }
        });

        var result = new RushResult { Enabled = true };
        if (!File.Exists(AppPaths.MihomoExe) || string.IsNullOrWhiteSpace(_settings.Current.ActiveProfileId))
        {
            result.Message = "Ускоренный канал включён. Загрузите ядро, выберите профиль и запустите подключение.";
            return result;
        }

        if (Running)
            await RestartAsync(ct);
        else
        {
            result.Message = "Ускоренный канал включён. Запустите ядро.";
            return result;
        }

        var port = _settings.Current.ExternalPort;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + ApiSecret);

        // Put PROXY on RUSH group
        try
        {
            using var sel = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/proxies/PROXY");
            sel.Content = new StringContent("{\"name\":\"RUSH\"}", System.Text.Encoding.UTF8, "application/json");
            using var selRes = await http.SendAsync(sel, ct);
            if (selRes.IsSuccessStatusCode || (int)selRes.StatusCode == 204)
                result.Lane = "RUSH";
        }
        catch { /* group may be missing if no fast nodes */ }

        var url = _settings.Current.LatencyUrl;
        var timeout = Math.Clamp(_settings.Current.LatencyTimeoutMs, 2000, 8000);
        string? best = null;
        var bestDelay = int.MaxValue;

        foreach (var group in new[] { "RUSH", "AUTO", "PROXY" })
        {
            try
            {
                var u = $"http://127.0.0.1:{port}/group/{Uri.EscapeDataString(group)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeout}";
                using var res = await http.GetAsync(u, ct);
                if (!res.IsSuccessStatusCode) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                    var d = prop.Value.GetInt32();
                    if (d <= 0 || d >= 10000) continue;
                    if (d < bestDelay)
                    {
                        bestDelay = d;
                        best = prop.Name;
                    }
                }
                if (best != null && group == "RUSH") break;
            }
            catch { /* next */ }
        }

        if (best != null)
        {
            try
            {
                // Select best leaf inside RUSH/AUTO if possible; also set PROXY to that node for immediacy
                foreach (var group in new[] { result.Lane == "RUSH" ? "RUSH" : "AUTO", "PROXY" })
                {
                    using var put = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/proxies/{Uri.EscapeDataString(group)}");
                    put.Content = new StringContent($"{{\"name\":{System.Text.Json.JsonSerializer.Serialize(best)}}}", System.Text.Encoding.UTF8, "application/json");
                    await http.SendAsync(put, ct);
                }
                result.Node = best;
                result.DelayMs = bestDelay;
                result.Message = $"Ускоренный канал активен. Выбран сервер {best}, задержка {bestDelay} мс.";
            }
            catch
            {
                result.Message = "Ускоренный канал активен. Автоматический выбор сервера не выполнен.";
            }
        }
        else
        {
            result.Message = result.Lane == "RUSH"
                ? "Ускоренный канал активен. Серверы ещё проверяются. Запустите проверку задержки."
                : "Ускоренный канал активен. В подписке недостаточно быстрых протоколов, используется автоматический выбор.";
        }

        return result;
    }

    /// <summary>
    /// Adaptive memory: foreign hosts currently going DIRECT get promoted into custom PROXY rules.
    /// Survives restarts — learns what your ISP lets slip past Re:filter.
    /// </summary>
    public async Task<LearnResult> LearnBypassAsync(CancellationToken ct = default)
    {
        var result = new LearnResult();
        if (!Running)
        {
            result.Message = "Сначала запустите ядро.";
            return result;
        }

        var port = _settings.Current.ExternalPort;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + ApiSecret);
        using var res = await http.GetAsync($"http://127.0.0.1:{port}/connections", ct);
        res.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("connections", out var conns))
        {
            foreach (var c in conns.EnumerateArray())
            {
                var host = "";
                if (c.TryGetProperty("metadata", out var meta))
                {
                    if (meta.TryGetProperty("host", out var h)) host = h.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(host) && meta.TryGetProperty("destinationIP", out var ip))
                        host = ip.GetString() ?? "";
                }
                var proxy = c.TryGetProperty("chains", out var chains) && chains.GetArrayLength() > 0
                    ? chains[0].GetString() ?? ""
                    : "";
                var rule = c.TryGetProperty("rule", out var r) ? r.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(host)) continue;
                if (host.EndsWith(".ru", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".su", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".xn--p1ai", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsPrivateHost(host)) continue;

                // Learned when traffic is DIRECT / MATCH but host looks foreign
                var viaDirect = proxy.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
                    || rule.Contains("DIRECT", StringComparison.OrdinalIgnoreCase)
                    || rule.Equals("Match", StringComparison.OrdinalIgnoreCase);
                if (!viaDirect) continue;

                var root = RootDomain(host);
                if (!string.IsNullOrEmpty(root)) found.Add(root);
            }
        }

        if (found.Count == 0)
        {
            result.Message = "Подходящих доменов для сохранения маршрута не найдено.";
            return result;
        }

        var existing = (_settings.Current.CustomProxyRules ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        foreach (var d in found.OrderBy(x => x))
        {
            if (existing.Contains(d)) continue;
            existing.Add(d);
            added.Add(d);
        }

        if (added.Count == 0)
        {
            result.Message = "Обнаруженные домены уже есть в списке маршрутов.";
            result.Domains = found.ToList();
            return result;
        }

        _settings.Patch(s => s.CustomProxyRules = string.Join('\n', existing.OrderBy(x => x)));
        // Persist rule file used by ConfigBuilder
        try
        {
            await RuleListService.EnsureAsync(_settings.Current, ct);
        }
        catch { /* ignore */ }

        if (Running) await RestartAsync(ct);

        result.Ok = true;
        result.Added = added.Count;
        result.Domains = added;
        result.Message = $"Сохранено доменов: {added.Count}. Ядро перезапущено.";
        return result;
    }

    static bool IsPrivateHost(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
            return IPAddress.IsLoopback(ip) || ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && (host.StartsWith("10.") || host.StartsWith("192.168.") || host.StartsWith("172."));
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }

    static string RootDomain(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith('[') || IPAddress.TryParse(host, out _)) return "";
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return host;
        // foo.bar.co.uk-style: keep last 2 labels (good enough for learning)
        return parts[^2] + "." + parts[^1];
    }

    public async Task<CoreStatus> RestartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            StopUnlocked();
            await Task.Delay(250, ct);
        }
        finally { _lifecycle.Release(); }
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
        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.MihomoExe,
                Arguments = "-v",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (p == null) return "";
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(true); } catch { /* ignore */ }
            }
            _coreVersion = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }
        catch { _coreVersion = ""; }
        finally { try { p?.Dispose(); } catch { /* ignore */ } }
        return _coreVersion;
    }

    public async Task<Dictionary<string, int>> TestDelaysAsync(string url, int timeoutMs, CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!Running) return result;

        var port = _settings.Current.ExternalPort;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + ApiSecret);

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

    /// <summary>Hot-swap mixed/socks/api ports, skip occupied ones, restart core and refresh system proxy.</summary>
    public async Task<PortApplyResult> ApplyPortsAsync(int mixed, int socks, int external, CancellationToken ct = default)
    {
        var result = new PortApplyResult { MixedPort = mixed, SocksPort = socks, ExternalPort = external };
        if (!ValidPort(mixed) || !ValidPort(socks) || !ValidPort(external))
        {
            result.Message = "Порты должны быть от 1024 до 65535.";
            return result;
        }
        if (mixed == socks || mixed == external || socks == external)
        {
            result.Message = "Mixed, SOCKS и API должны быть разными.";
            return result;
        }

        var wasRunning = Running;
        var wantProxy = _settings.Current.SystemProxy && !_settings.Current.TunEnabled;
        if (wasRunning) Stop();

        var busy = new List<int>();
        foreach (var p in new[] { mixed, socks, external })
        {
            if (!PortFree(p)) busy.Add(p);
        }
        if (busy.Count > 0)
        {
            result.Message = "Порт занят другим приложением: " + string.Join(", ", busy) + ". Выберите другой набор.";
            if (wasRunning)
            {
                try { await StartAsync(ct); } catch { /* keep error */ }
            }
            return result;
        }

        _settings.Patch(s =>
        {
            s.MixedPort = mixed;
            s.SocksPort = socks;
            s.ExternalPort = external;
        });

        if (wantProxy)
        {
            try { SystemProxy.Set(true, mixed); } catch { /* ignore */ }
        }

        if (wasRunning)
        {
            var st = await StartAsync(ct);
            result.Ok = st.Running && st.Message != "admin-required";
            result.Message = result.Ok
                ? $"Порты изменены. Прокси {mixed}. Ядро перезапущено."
                : (string.IsNullOrWhiteSpace(st.Message) ? "Не удалось перезапустить ядро" : st.Message);
            return result;
        }

        result.Ok = true;
        result.Message = $"Порты сохранены: {mixed}, {socks}, {external}. Запустите ядро.";
        return result;
    }

    static bool ValidPort(int p) => p is >= 1024 and <= 65535;

    static bool PortFree(int port)
    {
        System.Net.Sockets.TcpListener? listener = null;
        try
        {
            listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { /* ignore */ }
        }
    }
}
