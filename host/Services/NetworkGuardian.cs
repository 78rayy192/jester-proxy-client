using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Jester.Host.Models;

namespace Jester.Host.Services;

/// <summary>
/// Background intelligence: adaptive lane, warm start, exit consistency,
/// latency budget, smart-direct learning, DNS rotate, dead-man fail-closed.
/// </summary>
public sealed class NetworkGuardian : IDisposable
{
    readonly SettingsStore _settings;
    readonly CoreManager _core;
    readonly ConcurrentQueue<int> _rttSamples = new();
    CancellationTokenSource? _cts;
    Task? _loop;
    TcpListener? _sink;
    int _sinkPort;
    DateTimeOffset _lastExitCheck = DateTimeOffset.MinValue;
    DateTimeOffset _lastLaneCheck = DateTimeOffset.MinValue;
    DateTimeOffset _lastSmartDirect = DateTimeOffset.MinValue;
    DateTimeOffset _lastBudgetAction = DateTimeOffset.MinValue;
    string _lane = "AUTO";
    readonly object _armGate = new();
    readonly object _sessionGate = new();
    readonly SemaphoreSlim _warming = new(1, 1);
    readonly SocketsHttpHandler _apiHandler = new() { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
    CancellationTokenSource? _session;

    public NetworkGuardian(SettingsStore settings, CoreManager core)
    {
        _settings = settings;
        _core = core;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token));
    }

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                CoreManager.AppendLogPublic("guardian: " + ex.Message);
            }

            try { await Task.Delay(5000, ct); }
            catch { break; }
        }
    }

    async Task TickAsync(CancellationToken ct)
    {
        var s = _settings.Current;

        if (s.DnsDiversity && s.DnsRotateHours > 0)
        {
            if (s.LastDnsRotateAt is null)
            {
                _settings.Patch(x => x.LastDnsRotateAt = DateTimeOffset.UtcNow);
            }
            else if (DateTimeOffset.UtcNow - s.LastDnsRotateAt > TimeSpan.FromHours(Math.Max(1, s.DnsRotateHours))
                     && _core.Running)
            {
                _settings.Patch(x => NetworkPersonas.RotateDnsInto(x, advance: true));
                try { await _core.RestartIfRunningAsync(ct); }
                catch { /* ignore */ }
                Note("DNS pool rotated");
                return;
            }
        }

        if (!_core.Running) return;

        if (s.AdaptiveLane && s.RfRushMode)
        {
            var interval = Math.Max(20, s.AdaptiveLaneIntervalSec);
            if (DateTimeOffset.UtcNow - _lastLaneCheck > TimeSpan.FromSeconds(interval))
            {
                _lastLaneCheck = DateTimeOffset.UtcNow;
                await AdaptiveLaneAsync(s, ct);
            }
        }

        if (s.LatencyBudgetEnabled)
            await LatencyBudgetAsync(s, ct);

        if (s.ExitConsistencyCheck)
        {
            var every = Math.Max(45, s.ExitCheckIntervalSec);
            if (DateTimeOffset.UtcNow - _lastExitCheck > TimeSpan.FromSeconds(every))
            {
                _lastExitCheck = DateTimeOffset.UtcNow;
                await ExitConsistencyAsync(s, ct);
            }
        }

        if (s.SmartDirectMap && !s.StealthMode && DateTimeOffset.UtcNow - _lastSmartDirect > TimeSpan.FromMinutes(3))
        {
            _lastSmartDirect = DateTimeOffset.UtcNow;
            await SmartDirectLearnAsync(s, ct);
        }
    }

    public async Task WarmStartAsync(CancellationToken ct = default)
    {
        var s = _settings.Current;
        if (!s.WarmStart || !_core.Running || !await _warming.WaitAsync(0, ct)) return;
        try
        {
            var port = s.ExternalPort;
            using var http = ClashHttp();
            string? warmedGroup = null;
            var groups = s.PreferRushLane && s.RfRushMode ? new[] { "RUSH", "AUTO" } : new[] { "AUTO" };
            foreach (var group in groups)
            {
                try
                {
                    var url = s.LatencyUrl;
                    var timeout = Math.Clamp(s.LatencyTimeoutMs, 2000, 8000);
                    var u = $"http://127.0.0.1:{port}/group/{Uri.EscapeDataString(group)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeout}";
                    using var res = await http.GetAsync(u, ct);
                    if (!res.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject()
                        .Any(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var delay) && delay > 0))
                    {
                        warmedGroup = group;
                        break; // Overlapping groups must not retest the same subscription three times.
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* group may be missing */ }
            }

            if (warmedGroup == "RUSH" && _core.Running)
            {
                var selected = await CurrentProxySelectionAsync(ct);
                if (selected is "AUTO" or "RUSH")
                    await SelectProxyChildAsync("RUSH", ct);
            }
            _lastLaneCheck = DateTimeOffset.UtcNow;
            Note(warmedGroup == null ? "Warm start: no responding servers" : "Warm start completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            CoreManager.AppendLogPublic("warm-start: " + ex.Message);
        }
        finally { _warming.Release(); }
    }

    async Task AdaptiveLaneAsync(AppSettings s, CancellationToken ct)
    {
        try
        {
            var current = await CurrentProxySelectionAsync(ct) ?? _lane;
            if (current is not ("AUTO" or "RUSH")) return; // Preserve an explicitly selected server.
            var delays = await Task.WhenAll(GroupBestDelayAsync("RUSH", ct), GroupBestDelayAsync("AUTO", ct));
            var rushDelay = delays[0];
            var autoDelay = delays[1];

            var pick = rushDelay > 0 && rushDelay <= s.AdaptiveLaneBadRttMs ? "RUSH" : "AUTO";
            if (rushDelay > 0 && autoDelay > 0)
            {
                if (rushDelay > s.AdaptiveLaneBadRttMs && autoDelay + 40 < rushDelay)
                    pick = "AUTO";
                else if (rushDelay > 0 && rushDelay <= s.AdaptiveLaneGoodRttMs)
                    pick = "RUSH";
                else if (current.Equals("RUSH", StringComparison.OrdinalIgnoreCase) && rushDelay <= s.AdaptiveLaneBadRttMs)
                    pick = "RUSH";
            }
            else if (rushDelay <= 0 && autoDelay > 0)
                pick = "AUTO";

            var measured = rushDelay > 0 ? rushDelay : autoDelay;
            if (measured > 0)
            {
                _rttSamples.Enqueue(measured);
                while (_rttSamples.Count > 24 && _rttSamples.TryDequeue(out _)) { }
                _settings.Patch(x =>
                {
                    x.LastMeasuredRttMs = measured;
                    x.AdaptiveLaneState = pick;
                });
            }

            if (!pick.Equals(_lane, StringComparison.OrdinalIgnoreCase)
                || !current.Equals(pick, StringComparison.OrdinalIgnoreCase))
            {
                await SelectProxyChildAsync(pick, ct);
                _lane = pick;
                Note($"Adaptive lane → {pick} ({measured} ms)");
            }
        }
        catch (Exception ex)
        {
            CoreManager.AppendLogPublic("adaptive-lane: " + ex.Message);
        }
    }

    async Task LatencyBudgetAsync(AppSettings s, CancellationToken ct)
    {
        if (_rttSamples.Count < 5) return;
        var arr = _rttSamples.ToArray().OrderBy(x => x).ToArray();
        var idx = (int)Math.Ceiling(arr.Length * 0.95) - 1;
        idx = Math.Clamp(idx, 0, arr.Length - 1);
        var p95 = arr[idx];
        if (p95 <= s.LatencyBudgetMs) return;
        if (DateTimeOffset.UtcNow - _lastBudgetAction < TimeSpan.FromSeconds(30)) return;
        _lastBudgetAction = DateTimeOffset.UtcNow;

        Note($"Latency budget exceeded: p95 {p95} > {s.LatencyBudgetMs} ms");
        if (!string.Equals(s.LatencyBudgetAction, "switch", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var selected = await CurrentProxySelectionAsync(ct);
            if (selected is not ("AUTO" or "RUSH")) { _rttSamples.Clear(); return; }
            // Prefer switching leaf inside current lane
            var group = selected;
            var best = await BestNodeInGroupAsync(group, ct);
            if (best == null) return;
            try { await PutProxyAsync(group, best.Value.name, ct); }
            catch (HttpRequestException) { /* URL-test groups may reject manual child selection. */ }
            await PutProxyAsync("PROXY", best.Value.name, ct);
            Note($"Latency budget: switched to {best.Value.name} ({best.Value.delay} ms)");
            _rttSamples.Clear();
            _rttSamples.Enqueue(best.Value.delay);
        }
        catch (Exception ex)
        {
            CoreManager.AppendLogPublic("latency-budget: " + ex.Message);
        }
    }

    async Task ExitConsistencyAsync(AppSettings s, CancellationToken ct)
    {
        try
        {
            // Via mixed port so we see tunnel egress
            var handler = new HttpClientHandler { UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{s.MixedPort}") };
            using var proxied = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            var ip = (await proxied.GetStringAsync("https://api.ipify.org", ct)).Trim();
            if (string.IsNullOrWhiteSpace(ip)) return;

            string? org = null;
            try
            {
                var info = await proxied.GetStringAsync("https://ipinfo.io/json", ct);
                using var doc = JsonDocument.Parse(info);
                if (doc.RootElement.TryGetProperty("org", out var o)) org = o.GetString();
            }
            catch { /* optional */ }

            var prev = s.LastKnownExitIp;
            _settings.Patch(x =>
            {
                x.LastKnownExitIp = ip;
                if (!string.IsNullOrWhiteSpace(org)) x.LastKnownExitOrg = org;
            });

            if (string.IsNullOrWhiteSpace(prev))
            {
                Note($"Exit IP baseline {ip}");
                return;
            }

            if (string.Equals(prev, ip, StringComparison.OrdinalIgnoreCase))
                return;

            Note($"Exit IP changed {prev} → {ip}");
            var action = (s.ExitCheckAction ?? "switch").ToLowerInvariant();
            if (action == "warn") return;
            if (action == "panic")
            {
                await _core.PhantomPanicAsync(ct);
                return;
            }

            // switch: re-pick best node
            var best = await BestNodeInGroupAsync(_lane == "RUSH" ? "RUSH" : "AUTO", ct);
            if (best != null)
            {
                await PutProxyAsync("PROXY", best.Value.name, ct);
                Note($"Exit consistency: switched to {best.Value.name}");
            }
        }
        catch (Exception ex)
        {
            CoreManager.AppendLogPublic("exit-check: " + ex.Message);
            // Transient API failures must not trigger panic — only confirmed IP changes above do.
        }
    }

    async Task SmartDirectLearnAsync(AppSettings s, CancellationToken ct)
    {
        try
        {
            using var http = ClashHttp();
            using var res = await http.GetAsync($"http://127.0.0.1:{s.ExternalPort}/connections", ct);
            if (!res.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("connections", out var conns)) return;

            var bumps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in conns.EnumerateArray())
            {
                var host = "";
                if (c.TryGetProperty("metadata", out var meta))
                {
                    if (meta.TryGetProperty("host", out var h)) host = h.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(host)) continue;

                var proxy = c.TryGetProperty("chains", out var chains) && chains.GetArrayLength() > 0
                    ? chains[0].GetString() ?? ""
                    : "";
                if (!proxy.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) continue;

                // Prefer domestic / local-looking hosts for smart direct promotion
                var root = RootDomain(host);
                if (string.IsNullOrEmpty(root)) continue;
                if (!(root.EndsWith(".ru", StringComparison.OrdinalIgnoreCase)
                      || root.EndsWith(".su", StringComparison.OrdinalIgnoreCase)
                      || root.EndsWith(".рф", StringComparison.OrdinalIgnoreCase)
                      || root.Contains("yandex", StringComparison.OrdinalIgnoreCase)
                      || root is "vk.com" or "vk.ru" or "userapi.com"
                      || root.Contains("mail.ru", StringComparison.OrdinalIgnoreCase)
                      || root.Contains("sber", StringComparison.OrdinalIgnoreCase)
                      || root.Contains("gosuslugi", StringComparison.OrdinalIgnoreCase)))
                    continue;

                bumps[root] = bumps.GetValueOrDefault(root) + 1;
            }

            if (bumps.Count == 0) return;

            var promoted = new List<string>();
            _settings.Patch(x =>
            {
                foreach (var (dom, n) in bumps)
                {
                    x.SmartDirectHits.TryGetValue(dom, out var cur);
                    var next = cur + n;
                    x.SmartDirectHits[dom] = next;
                    if (next >= Math.Max(2, x.SmartDirectMinHits)
                        && !x.SmartDirectDomains.Contains(dom, StringComparer.OrdinalIgnoreCase))
                    {
                        x.SmartDirectDomains.Add(dom);
                        promoted.Add(dom);
                    }
                }
                if (promoted.Count > 0)
                {
                    var set = (x.CustomDirectRules ?? "")
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var d in promoted) set.Add(d);
                    x.CustomDirectRules = string.Join('\n', set.OrderBy(y => y));
                }
            });

            if (promoted.Count > 0)
            {
                try { await RuleListService.EnsureAsync(_settings.Current, ct); } catch { /* ignore */ }
                Note("Smart direct +" + string.Join(", ", promoted));
                if (promoted.Count >= 3)
                {
                    try { await _core.RestartIfRunningAsync(ct); } catch { /* ignore */ }
                }
            }
        }
        catch (Exception ex)
        {
            CoreManager.AppendLogPublic("smart-direct: " + ex.Message);
        }
    }

    /// <summary>Called from CoreManager when process dies unexpectedly.</summary>
    public void OnUnexpectedCoreExit()
    {
        var s = _settings.Current;
        if (!s.DeadManSwitch) return;
        ArmFailClosed();
        Note("Dead-man armed (core exit)");
    }

    /// <summary>Called after successful Start.</summary>
    public void OnCoreOnline()
    {
        if (_settings.Current.DeadManArmed)
            DisarmFailClosed(keepProxy: true);
        CancellationToken ct;
        lock (_sessionGate)
        {
            _session?.Cancel();
            _session?.Dispose();
            _session = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
            ct = _session.Token;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, ct);
                await WarmStartAsync(ct);
            }
            catch { /* ignore */ }
        });
    }

    public void OnCoreStopped()
    {
        lock (_sessionGate)
        {
            _session?.Cancel();
            _session?.Dispose();
            _session = null;
        }
        _rttSamples.Clear();
    }

    public void ArmFailClosed()
    {
        lock (_armGate)
        {
            EnsureSink();
            try { SystemProxy.Set(true, _sinkPort); } catch { /* ignore */ }
            _settings.Patch(s => s.DeadManArmed = true);
        }
    }

    public void DisarmFailClosed(bool keepProxy = false)
    {
        lock (_armGate)
        {
            try
            {
                if (!keepProxy) SystemProxy.Set(false, 0);
            }
            catch { /* ignore */ }
            _settings.Patch(s => s.DeadManArmed = false);
            Note("Dead-man disarmed");
        }
    }

    void EnsureSink()
    {
        if (_sink != null) return;
        _sink = new TcpListener(IPAddress.Loopback, 0);
        _sink.Start();
        _sinkPort = ((IPEndPoint)_sink.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (_sink != null)
            {
                try
                {
                    var c = await _sink.AcceptTcpClientAsync();
                    c.Close();
                }
                catch { break; }
            }
        });
    }

    HttpClient ClashHttp()
    {
        var http = new HttpClient(_apiHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + _core.ApiSecret);
        return http;
    }

    async Task<int> GroupBestDelayAsync(string group, CancellationToken ct)
    {
        var best = await BestNodeInGroupAsync(group, ct);
        return best?.delay ?? -1;
    }

    async Task<(string name, int delay)?> BestNodeInGroupAsync(string group, CancellationToken ct)
    {
        var port = _settings.Current.ExternalPort;
        var url = _settings.Current.LatencyUrl;
        var timeout = Math.Clamp(_settings.Current.LatencyTimeoutMs, 2000, 8000);
        using var http = ClashHttp();
        var u = $"http://127.0.0.1:{port}/group/{Uri.EscapeDataString(group)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeout}";
        using var res = await http.GetAsync(u, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        string? best = null;
        var bestDelay = int.MaxValue;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Number) continue;
            var d = prop.Value.GetInt32();
            if (d <= 0 || d >= 10000) continue;
            if (d < bestDelay) { bestDelay = d; best = prop.Name; }
        }
        return best == null ? null : (best, bestDelay);
    }

    async Task SelectProxyChildAsync(string child, CancellationToken ct)
    {
        await PutProxyAsync("PROXY", child, ct);
    }

    async Task PutProxyAsync(string group, string name, CancellationToken ct)
    {
        var port = _settings.Current.ExternalPort;
        using var http = ClashHttp();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/proxies/{Uri.EscapeDataString(group)}");
        put.Content = new StringContent($"{{\"name\":{JsonSerializer.Serialize(name)}}}", System.Text.Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(put, ct);
        response.EnsureSuccessStatusCode();
    }

    async Task<string?> CurrentProxySelectionAsync(CancellationToken ct)
    {
        try
        {
            var port = _settings.Current.ExternalPort;
            using var http = ClashHttp();
            using var res = await http.GetAsync($"http://127.0.0.1:{port}/proxies/PROXY", ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("now", out var now))
                return now.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    void Note(string msg)
    {
        _settings.Patch(s => s.GuardianMessage = msg);
        CoreManager.AppendLogPublic("guardian: " + msg);
    }

    static string RootDomain(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith('[') || IPAddress.TryParse(host, out _)) return "";
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return host;
        return parts[^2] + "." + parts[^1];
    }

    public void Dispose()
    {
        OnCoreStopped();
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _sink?.Stop(); } catch { /* ignore */ }
        _sink = null;
        try { _loop?.Wait(1500); } catch { /* ignore */ }
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
        _apiHandler.Dispose();
    }
}
