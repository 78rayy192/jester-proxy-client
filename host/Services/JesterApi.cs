using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Jester.Host.Models;

namespace Jester.Host.Services;

public sealed class JesterApi : IDisposable
{
    public const int Port = 37841;
    public static string Origin => $"http://127.0.0.1:{Port}";

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    readonly HttpListener _listener = new();
    readonly SettingsStore _settings;
    readonly ProfileStore _profiles;
    readonly CoreManager _core;
    readonly HttpClient _clash = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    CancellationTokenSource? _cts;
    Task? _loop;

    public JesterApi(SettingsStore settings, ProfileStore profiles, CoreManager core)
    {
        _settings = settings;
        _profiles = profiles;
        _core = core;
    }

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Listen(_cts.Token));
    }

    async Task Listen(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(ct); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url?.AbsolutePath ?? "/";
        try
        {
            if (!LocalApiPolicy.IsAllowed(req.Headers["Origin"], req.Headers["Sec-Fetch-Site"],
                    req.Headers["Host"], LocalApiPolicy.Development))
            {
                await WriteJson(ctx, 403, new { error = "Доступ разрешён только интерфейсу Jester." });
                return;
            }
            AddCors(ctx);
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                ctx.Response.Headers["Cache-Control"] = "no-store";
            if (req.HasEntityBody && req.HttpMethod is not ("GET" or "HEAD" or "OPTIONS")
                && !(req.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJson(ctx, 415, new { error = "Ожидается application/json." });
                return;
            }
            if (req.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            if (path.StartsWith("/clash", StringComparison.OrdinalIgnoreCase))
            {
                await ProxyClash(ctx);
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await RouteApi(ctx, path);
                return;
            }

            await ServeUi(ctx, path);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            try { await WriteJson(ctx, 400, new { error = ex.Message }); } catch { ctx.Response.Abort(); }
        }
        catch (Exception ex)
        {
            try { await WriteJson(ctx, 500, new { error = ex.Message }); } catch { ctx.Response.Abort(); }
        }
    }

    async Task RouteApi(HttpListenerContext ctx, string path)
    {
        var method = ctx.Request.HttpMethod;
        switch ($"{method} {path}")
        {
            case "GET /api/health":
                await WriteJson(ctx, 200, new { ok = true, name = "Jester" });
                return;
            case "GET /api/status":
                await WriteJson(ctx, 200, _core.Snapshot());
                return;
            case "GET /api/settings":
                await WriteJson(ctx, 200, _settings.Current);
                return;
            case "PUT /api/settings":
                var next = await ReadJson<AppSettings>(ctx) ?? throw new InvalidOperationException("Пустые настройки.");
                var savedSettings = _settings.Save(next);
                WindowActions.ApplyAppearance?.Invoke();
                await WriteJson(ctx, 200, savedSettings);
                return;
            case "GET /api/appearance":
                await WriteJson(ctx, 200, _settings.Current.Appearance);
                return;
            case "PUT /api/appearance":
                var appearance = await ReadJson<AppearanceSettings>(ctx) ?? new();
                var savedAppearance = _settings.Patch(s => s.Appearance = appearance).Appearance;
                WindowActions.ApplyAppearance?.Invoke();
                await WriteJson(ctx, 200, savedAppearance);
                return;
            case "GET /api/appearance/wallpaper":
                await ServeWallpaper(ctx);
                return;
            case "POST /api/appearance/pick-wallpaper":
            {
                var picked = WindowActions.PickImage?.Invoke();
                if (string.IsNullOrWhiteSpace(picked))
                {
                    await WriteJson(ctx, 200, new { cancelled = true });
                    return;
                }
                var nextAppearance = _settings.Patch(s =>
                {
                    s.Appearance.BackgroundImage = picked;
                    s.Appearance.BackgroundStyle = "image";
                }).Appearance;
                WindowActions.ApplyAppearance?.Invoke();
                await WriteJson(ctx, 200, nextAppearance);
                return;
            }
            case "GET /api/profiles":
                await WriteJson(ctx, 200, _profiles.List());
                return;
            case "POST /api/profiles":
                var create = await ReadJson<CreateProfileRequest>(ctx) ?? new();
                var created = await _profiles.CreateAsync(create);
                if (string.IsNullOrWhiteSpace(_settings.Current.ActiveProfileId))
                    _settings.Patch(s => s.ActiveProfileId = created.Id);
                await WriteJson(ctx, 200, created);
                return;
            case "POST /api/profiles/update-all":
            {
                var (updated, failed, errors) = await _profiles.UpdateAllSubscriptionsAsync();
                _settings.Patch(s => s.LastSubscriptionUpdateAt = DateTimeOffset.UtcNow);
                if (_core.Running && updated > 0)
                {
                    await _core.RestartIfRunningAsync();
                }
                await WriteJson(ctx, 200, new { updated, failed, errors });
                return;
            }
            case "GET /api/app/update":
                await WriteJson(ctx, 200, await AppUpdateChecker.CheckAsync());
                return;
            case "GET /api/settings/export":
                await WriteJson(ctx, 200, _settings.Current);
                return;
            case "POST /api/settings/import":
            {
                var imported = await ReadJson<AppSettings>(ctx) ?? throw new InvalidOperationException("Пустой файл настроек.");
                var savedImport = _settings.Save(imported);
                WindowActions.ApplyAppearance?.Invoke();
                await WriteJson(ctx, 200, savedImport);
                return;
            }
            case "POST /api/presets/speed":
                await WriteJson(ctx, 200, ApplyPreset("speed"));
                return;
            case "POST /api/presets/stability":
                await WriteJson(ctx, 200, ApplyPreset("stability"));
                return;
            case "POST /api/presets/protection":
                await WriteJson(ctx, 200, ApplyPreset("protection"));
                return;
            case "POST /api/persona/home":
            case "POST /api/persona/mobile":
            case "POST /api/persona/corporate":
            case "POST /api/persona/auto":
            {
                var persona = path.Split('/').Last();
                await WriteJson(ctx, 200, ApplyPersona(persona));
                return;
            }
            case "GET /api/guardian":
                await WriteJson(ctx, 200, new
                {
                    message = _settings.Current.GuardianMessage,
                    lane = _settings.Current.AdaptiveLaneState,
                    rttMs = _settings.Current.LastMeasuredRttMs,
                    deadManArmed = _settings.Current.DeadManArmed,
                    exitIp = _settings.Current.LastKnownExitIp,
                    exitOrg = _settings.Current.LastKnownExitOrg,
                    smartDirect = _settings.Current.SmartDirectDomains,
                    dnsPoolIndex = _settings.Current.DnsPoolIndex
                });
                return;
            case "POST /api/guardian/disarm":
                _core.Guardian?.DisarmFailClosed();
                await WriteJson(ctx, 200, new { ok = true, deadManArmed = _settings.Current.DeadManArmed });
                return;
            case "POST /api/guardian/warm":
                if (_core.Guardian != null) await _core.Guardian.WarmStartAsync();
                await WriteJson(ctx, 200, new { ok = true });
                return;
            case "POST /api/guardian/learn-direct":
                await WriteJson(ctx, 200, await LearnSmartDirectNow());
                return;
            case "GET /api/logs":
                var lines = int.TryParse(ctx.Request.QueryString["lines"], out var n) ? n : 250;
                await WriteJson(ctx, 200, new { text = CoreManager.ReadLogTail(lines) });
                return;
            case "POST /api/core/start":
                await WriteJson(ctx, 200, await _core.StartAsync());
                return;
            case "POST /api/core/stop":
                _core.Stop();
                await WriteJson(ctx, 200, _core.Snapshot());
                return;
            case "POST /api/core/restart":
                await WriteJson(ctx, 200, await _core.RestartAsync());
                return;
            case "POST /api/core/download":
                var channel = _settings.Current.CoreChannel;
                await _core.Downloader.InstallAsync(channel);
                await WriteJson(ctx, 200, _core.Snapshot());
                return;
            case "POST /api/core/elevate":
                _core.Elevate();
                await WriteJson(ctx, 200, new { ok = true });
                return;
            case "POST /api/sysproxy":
            {
                var body = await ReadJson<Dictionary<string, JsonElement>>(ctx) ?? new();
                var enable = body.TryGetValue("enable", out var e) && e.GetBoolean();
                if (enable)
                {
                    var needRestart = _core.Running && _settings.Current.TunEnabled;
                    _settings.Patch(s =>
                    {
                        s.SystemProxy = true;
                        s.TunEnabled = false;
                    });
                    if (needRestart)
                        await _core.RestartAsync();
                    else if (_core.Running)
                        SystemProxy.Set(true, _settings.Current.MixedPort);
                }
                else
                {
                    SystemProxy.Set(false, 0);
                    _settings.Patch(s => s.SystemProxy = false);
                }

                await WriteJson(ctx, 200, new
                {
                    enable = _settings.Current.SystemProxy,
                    tunEnabled = _settings.Current.TunEnabled,
                    windowsProxy = SystemProxy.IsEnabled(),
                    mixedPort = _settings.Current.MixedPort
                });
                return;
            }
            case "GET /api/sysproxy":
                await WriteJson(ctx, 200, new
                {
                    enable = _settings.Current.SystemProxy,
                    windowsProxy = SystemProxy.IsEnabled(),
                    mixedPort = _settings.Current.MixedPort
                });
                return;
            case "POST /api/proxies/test":
            {
                if (!_core.Running)
                {
                    await WriteJson(ctx, 400, new { error = "Ядро не запущено." });
                    return;
                }
                var q = ctx.Request.QueryString;
                var url = q["url"] ?? _settings.Current.LatencyUrl;
                var timeout = int.TryParse(q["timeout"], out var t) ? t : _settings.Current.LatencyTimeoutMs;
                var map = await _core.TestDelaysAsync(url, timeout);
                await WriteJson(ctx, 200, map);
                return;
            }
            case "GET /api/rules/status":
                await WriteJson(ctx, 200, RuleListService.Status(_settings.Current));
                return;
            case "POST /api/rules/update":
                await WriteJson(ctx, 200, await RuleListService.UpdateAsync(_settings.Current));
                return;
            case "GET /api/leak-check":
                await WriteJson(ctx, 200, await _core.LeakCheckAsync());
                return;
            case "POST /api/ghost/panic":
                await WriteJson(ctx, 200, await _core.PhantomPanicAsync());
                return;
            case "POST /api/ghost/rush":
                await WriteJson(ctx, 200, await _core.ActivateRushAsync());
                return;
            case "POST /api/ghost/learn":
                await WriteJson(ctx, 200, await _core.LearnBypassAsync());
                return;
            case "POST /api/ports":
            {
                var body = await ReadJson<Dictionary<string, JsonElement>>(ctx) ?? new();
                int Num(string k, int fallback) =>
                    body.TryGetValue(k, out var v) && v.TryGetInt32(out var n) ? n : fallback;
                var cur = _settings.Current;
                var applied = await _core.ApplyPortsAsync(
                    Num("mixedPort", cur.MixedPort),
                    Num("socksPort", cur.SocksPort),
                    Num("externalPort", cur.ExternalPort));
                await WriteJson(ctx, 200, applied);
                return;
            }
            case "POST /api/window/capture":
            {
                var body = await ReadJson<Dictionary<string, JsonElement>>(ctx) ?? new();
                if (body.TryGetValue("hide", out var h))
                    _settings.Patch(s => s.HideFromCapture = h.GetBoolean());
                WindowActions.ApplyCapture?.Invoke();
                await WriteJson(ctx, 200, new { hide = _settings.Current.HideFromCapture || _settings.Current.StealthMode });
                return;
            }
            case "POST /api/window/minimize":
                WindowActions.Minimize?.Invoke();
                await WriteJson(ctx, 200, new { ok = true });
                return;
            case "POST /api/window/maximize":
                WindowActions.ToggleMaximize?.Invoke();
                await WriteJson(ctx, 200, new { ok = true });
                return;
            case "POST /api/window/close":
                WindowActions.CloseOrTray?.Invoke(_settings.Current.CloseToTray);
                await WriteJson(ctx, 200, new { ok = true });
                return;
        }

        if (path.StartsWith("/api/profiles/") && path.EndsWith("/activate") && method == "POST")
        {
            var id = path["/api/profiles/".Length..^"/activate".Length];
            if (!ProfileStore.IsValidId(id) || _profiles.Get(id) == null)
            {
                await WriteJson(ctx, 404, new { error = "not found" });
                return;
            }
            _settings.Patch(s => s.ActiveProfileId = id);
            await _core.RestartIfRunningAsync();
            await WriteJson(ctx, 200, _settings.Current);
            return;
        }
        if (path.StartsWith("/api/profiles/") && path.EndsWith("/update") && method == "POST")
        {
            var id = path["/api/profiles/".Length..^"/update".Length];
            if (!ProfileStore.IsValidId(id))
            {
                await WriteJson(ctx, 400, new { error = "bad id" });
                return;
            }
            var updated = await _profiles.UpdateSubscriptionAsync(id);
            if (_core.Running && _settings.Current.ActiveProfileId == id)
                await _core.RestartIfRunningAsync();
            await WriteJson(ctx, 200, updated);
            return;
        }
        if (path.StartsWith("/api/profiles/") && method == "DELETE")
        {
            var id = path["/api/profiles/".Length..];
            if (!ProfileStore.IsValidId(id))
            {
                await WriteJson(ctx, 400, new { error = "bad id" });
                return;
            }
            var active = _settings.Current.ActiveProfileId == id;
            if (active) _core.Stop();
            _profiles.Delete(id);
            if (active) _settings.Patch(s => s.ActiveProfileId = "");
            await WriteJson(ctx, 200, new { ok = true });
            return;
        }
        if (path.StartsWith("/api/profiles/") && method == "PUT")
        {
            var id = path["/api/profiles/".Length..];
            if (!ProfileStore.IsValidId(id))
            {
                await WriteJson(ctx, 400, new { error = "bad id" });
                return;
            }
            var patch = await ReadJson<Dictionary<string, JsonElement>>(ctx) ?? new();
            await WriteJson(ctx, 200, _profiles.Patch(id, info =>
            {
                if (patch.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    var name = n.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(name)) info.Name = name.Trim();
                }
                if (patch.TryGetValue("fingerprintOverride", out var fp))
                {
                    info.FingerprintOverride = fp.ValueKind == JsonValueKind.String ? fp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(info.FingerprintOverride)) info.FingerprintOverride = null;
                }
                if (patch.TryGetValue("nameserverOverride", out var ns))
                {
                    if (ns.ValueKind == JsonValueKind.String)
                    {
                        var text = ns.GetString() ?? "";
                        info.NameserverOverride = text
                            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToList();
                        if (info.NameserverOverride.Count == 0) info.NameserverOverride = null;
                    }
                    else if (ns.ValueKind == JsonValueKind.Array)
                    {
                        info.NameserverOverride = ns.EnumerateArray()
                            .Select(x => x.GetString() ?? "")
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .ToList();
                        if (info.NameserverOverride.Count == 0) info.NameserverOverride = null;
                    }
                    else if (ns.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        info.NameserverOverride = null;
                }
            }));
            return;
        }

        await WriteJson(ctx, 404, new { error = "not found" });
    }

    async Task ProxyClash(HttpListenerContext ctx)
    {
        var settings = _settings.Current;
        var raw = ctx.Request.RawUrl ?? "/clash/";
        var rest = raw.StartsWith("/clash", StringComparison.OrdinalIgnoreCase) ? raw[6..] : raw;
        if (string.IsNullOrEmpty(rest)) rest = "/";
        var pathOnly = rest.Split('?', 2)[0];
        var qs = ctx.Request.Url?.Query ?? "";
        var url = $"http://127.0.0.1:{settings.ExternalPort}{pathOnly}{qs}";

        using var upstream = new HttpRequestMessage(new HttpMethod(ctx.Request.HttpMethod), url);
        upstream.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _core.ApiSecret);
        if (ctx.Request.ContentLength64 > 0)
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            var payload = await reader.ReadToEndAsync();
            var content = new StringContent(payload, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                (ctx.Request.ContentType ?? "application/json").Split(';')[0].Trim());
            upstream.Content = content;
        }

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
        requestCts.CancelAfter(TimeSpan.FromSeconds(30));
        using var res = await _clash.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, requestCts.Token);
        ctx.Response.StatusCode = (int)res.StatusCode;

        var contentType = res.Content.Headers.ContentType?.ToString() ?? "";
        var isStream = res.IsSuccessStatusCode && (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            || pathOnly.Equals("/traffic", StringComparison.OrdinalIgnoreCase)
            || pathOnly.Equals("/memory", StringComparison.OrdinalIgnoreCase)
            || pathOnly.Equals("/logs", StringComparison.OrdinalIgnoreCase));

        foreach (var h in res.Headers)
            TrySetHeader(ctx.Response.Headers, h.Key, string.Join(",", h.Value));
        foreach (var h in res.Content.Headers)
        {
            if (isStream && h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            TrySetHeader(ctx.Response.Headers, h.Key, string.Join(",", h.Value));
        }

        await using var src = await res.Content.ReadAsStreamAsync();
        if (isStream)
        {
            requestCts.CancelAfter(Timeout.InfiniteTimeSpan);
            ctx.Response.SendChunked = true;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            TrySetHeader(ctx.Response.Headers, "Cache-Control", "no-cache");
            TrySetHeader(ctx.Response.Headers, "X-Accel-Buffering", "no");
            await MihomoStream.WriteSseAsync(src, ctx.Response.OutputStream, requestCts.Token);
            ctx.Response.Close();
            return;
        }

        await src.CopyToAsync(ctx.Response.OutputStream, requestCts.Token);
        ctx.Response.Close();
    }

    async Task ServeWallpaper(HttpListenerContext ctx)
    {
        var raw = _settings.Current.Appearance.BackgroundImage?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw)
            || raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(raw))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var ext = Path.GetExtension(raw).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp"))
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = Mime(raw);
        TrySetHeader(ctx.Response.Headers, "Cache-Control", "no-store");
        await using var fs = File.OpenRead(raw);
        await fs.CopyToAsync(ctx.Response.OutputStream);
        ctx.Response.Close();
    }

    async Task ServeUi(HttpListenerContext ctx, string path)
    {
        if (path == "/") path = "/index.html";
        var uiRoot = Path.GetFullPath(AppPaths.UiFolder);
        if (!uiRoot.EndsWith(Path.DirectorySeparatorChar))
            uiRoot += Path.DirectorySeparatorChar;
        var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(uiRoot, relative));
        if (!full.StartsWith(uiRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            var index = Path.Combine(uiRoot, "index.html");
            if (File.Exists(index)) full = index;
            else
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                var html = "<!doctype html><meta charset=utf-8><body style='background:#0b0a0f;color:#f4ecd8;font-family:Segoe UI;padding:48px'><h1>Jester</h1><p>Соберите интерфейс: <code>cd ui && npm install && npm run build</code></p></body>";
                var bytes = Encoding.UTF8.GetBytes(html);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
                return;
            }
        }

        ctx.Response.ContentType = Mime(full);
        ctx.Response.StatusCode = 200;
        await using var fs = File.OpenRead(full);
        await fs.CopyToAsync(ctx.Response.OutputStream);
        ctx.Response.Close();
    }

    static void AddCors(HttpListenerContext ctx)
    {
        var origin = ctx.Request.Headers["Origin"];
        if (origin != null && LocalApiPolicy.IsTrustedOrigin(origin, LocalApiPolicy.Development))
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
            ctx.Response.Headers["Vary"] = "Origin";
        }
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,PATCH,DELETE,OPTIONS";
    }

    static void TrySetHeader(NameValueCollection headers, string key, string value)
    {
        if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) return;
        try { headers[key] = value; } catch { /* hop-by-hop */ }
    }

    static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };

    static async Task<T?> ReadJson<T>(HttpListenerContext ctx)
    {
        const int maximum = 8 * 1024 * 1024;
        if (ctx.Request.ContentLength64 > maximum) throw new ArgumentException("Запрос превышает 8 МиБ.");
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var builder = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            if (builder.Length + count > maximum) throw new ArgumentException("Запрос превышает 8 МиБ.");
            builder.Append(buffer, 0, count);
        }
        var text = builder.ToString();
        if (string.IsNullOrWhiteSpace(text)) return default;
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    static async Task WriteJson(HttpListenerContext ctx, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    AppSettings ApplyPersona(string name)
    {
        return _settings.Patch(s =>
        {
            s.NetworkPersona = name;
            s.PerformancePreset = "custom";
            NetworkPersonas.ApplyPersona(s);
        });
    }

    async Task<object> LearnSmartDirectNow()
    {
        // Force one smart-direct pass via connections snapshot (reuse learn logic lightly)
        if (!_core.Running) return new { ok = false, message = "Ядро не запущено." };
        var before = _settings.Current.SmartDirectDomains.Count;
        // Trigger by lowering interval barrier: call guardian tick path indirectly
        _settings.Patch(s => s.SmartDirectMinHits = Math.Max(2, s.SmartDirectMinHits));
        if (_core.Guardian != null)
        {
            // Warm path isn't enough — use existing LearnBypass opposite: promote RU DIRECT hits
            await Task.Delay(10);
        }
        // Manual promotion from current DIRECT RU hosts
        var result = await PromoteDirectAsync();
        return result;
    }

    async Task<object> PromoteDirectAsync()
    {
        try
        {
            var s = _settings.Current;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + _core.ApiSecret);
            using var res = await http.GetAsync($"http://127.0.0.1:{s.ExternalPort}/connections");
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("connections", out var conns))
            {
                foreach (var c in conns.EnumerateArray())
                {
                    var host = "";
                    if (c.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("host", out var h))
                        host = h.GetString() ?? "";
                    var proxy = c.TryGetProperty("chains", out var chains) && chains.GetArrayLength() > 0
                        ? chains[0].GetString() ?? "" : "";
                    if (!proxy.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(host)) continue;
                    if (!(host.EndsWith(".ru", StringComparison.OrdinalIgnoreCase)
                          || host.EndsWith(".su", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var parts = host.Trim().TrimEnd('.').Split('.');
                    if (parts.Length >= 2) found.Add(parts[^2] + "." + parts[^1]);
                }
            }
            if (found.Count == 0) return new { ok = false, message = "Нет подходящих DIRECT-доменов.", domains = Array.Empty<string>() };
            var added = new List<string>();
            _settings.Patch(x =>
            {
                foreach (var d in found)
                {
                    if (x.SmartDirectDomains.Contains(d, StringComparer.OrdinalIgnoreCase)) continue;
                    x.SmartDirectDomains.Add(d);
                    added.Add(d);
                }
                if (added.Count > 0)
                {
                    var set = (x.CustomDirectRules ?? "")
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var d in added) set.Add(d);
                    x.CustomDirectRules = string.Join('\n', set.OrderBy(y => y));
                }
            });
            if (added.Count == 0) return new { ok = true, message = "Домены уже в карте.", domains = found.ToList() };
            try { await RuleListService.EnsureAsync(_settings.Current); } catch { /* ignore */ }
            if (_core.Running) await _core.RestartAsync();
            return new { ok = true, message = $"Добавлено в Smart Direct: {added.Count}", domains = added };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    AppSettings ApplyPreset(string name)
    {
        return _settings.Patch(s =>
        {
            s.PerformancePreset = name;
            switch (name)
            {
                case "speed":
                    s.RfRushMode = true;
                    s.PreferRushLane = true;
                    s.TcpConcurrent = true;
                    s.UnifiedDelay = true;
                    s.HighPriority = true;
                    s.StealthMode = false;
                    s.SilentLogs = false;
                    s.LogLevel = "warning";
                    s.StrictRoute = false;
                    s.AdaptiveLane = true;
                    s.WarmStart = true;
                    s.SplitUdpPolicy = true;
                    s.PerAppFastPath = true;
                    s.NetworkPersona = "home";
                    break;
                case "stability":
                    s.RfRushMode = false;
                    s.PreferRushLane = false;
                    s.TcpConcurrent = true;
                    s.UnifiedDelay = true;
                    s.HighPriority = false;
                    s.StealthMode = true;
                    s.BlockQuic = true;
                    s.BlockStun = true;
                    s.DnsViaProxy = true;
                    s.SilentLogs = true;
                    s.StrictRoute = false;
                    s.AdaptiveLane = false;
                    s.LatencyBudgetEnabled = true;
                    s.LatencyBudgetMs = 320;
                    s.NetworkPersona = "auto";
                    break;
                case "protection":
                    s.RfRushMode = true;
                    s.PreferRushLane = true;
                    s.StealthMode = true;
                    s.BlockQuic = true;
                    s.BlockStun = true;
                    s.DnsViaProxy = true;
                    s.DnsRespectRules = true;
                    s.RandomFingerprint = true;
                    s.SilentLogs = true;
                    s.HideFromCapture = true;
                    s.StrictRoute = true;
                    s.UseMrsRules = true;
                    s.TrafficPadding = true;
                    s.DnsDiversity = true;
                    s.DnsStrictNoFallback = true;
                    s.ExitConsistencyCheck = true;
                    s.DeadManSwitch = true;
                    s.NetworkPersona = "corporate";
                    break;
            }
        });
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener.Stop(); } catch { /* ignore */ }
        _clash.Dispose();
    }
}

public static class WindowActions
{
    public static Action? Minimize { get; set; }
    public static Action? ToggleMaximize { get; set; }
    public static Action<bool>? CloseOrTray { get; set; }
    public static Action? Drag { get; set; }
    public static Action? ApplyCapture { get; set; }
    public static Action? ApplyAppearance { get; set; }
    public static Func<string?>? PickImage { get; set; }
}
