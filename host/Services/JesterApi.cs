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
    readonly HttpClient _clash = new() { Timeout = Timeout.InfiniteTimeSpan };
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
            AddCors(ctx);
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
                await WriteJson(ctx, 200, _settings.Save(next));
                return;
            case "GET /api/appearance":
                await WriteJson(ctx, 200, _settings.Current.Appearance);
                return;
            case "PUT /api/appearance":
                var appearance = await ReadJson<AppearanceSettings>(ctx) ?? new();
                await WriteJson(ctx, 200, _settings.Patch(s => s.Appearance = appearance).Appearance);
                return;
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
                    else
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
                    await WriteJson(ctx, 400, new { error = "Ядро не запущено" });
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
            if (_profiles.Get(id) == null) { await WriteJson(ctx, 404, new { error = "not found" }); return; }
            _settings.Patch(s => s.ActiveProfileId = id);
            await WriteJson(ctx, 200, _settings.Current);
            return;
        }
        if (path.StartsWith("/api/profiles/") && path.EndsWith("/update") && method == "POST")
        {
            var id = path["/api/profiles/".Length..^"/update".Length];
            await WriteJson(ctx, 200, await _profiles.UpdateSubscriptionAsync(id));
            return;
        }
        if (path.StartsWith("/api/profiles/") && method == "DELETE")
        {
            var id = path["/api/profiles/".Length..];
            var active = _settings.Current.ActiveProfileId == id;
            _profiles.Delete(id);
            if (active) _settings.Patch(s => s.ActiveProfileId = "");
            await WriteJson(ctx, 200, new { ok = true });
            return;
        }
        if (path.StartsWith("/api/profiles/") && method == "PUT")
        {
            var id = path["/api/profiles/".Length..];
            var patch = await ReadJson<Dictionary<string, string>>(ctx) ?? new();
            var name = patch.GetValueOrDefault("name") ?? "";
            await WriteJson(ctx, 200, _profiles.Rename(id, name));
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
        // Rebuild query from QueryString to avoid double-encoding issues on delay URLs
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

        using var res = await _clash.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead);
        ctx.Response.StatusCode = (int)res.StatusCode;

        var contentType = res.Content.Headers.ContentType?.ToString() ?? "";
        var isStream = contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            || pathOnly.Equals("/traffic", StringComparison.OrdinalIgnoreCase)
            || pathOnly.Equals("/memory", StringComparison.OrdinalIgnoreCase)
            || pathOnly.StartsWith("/logs", StringComparison.OrdinalIgnoreCase);

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
            ctx.Response.SendChunked = true;
            if (string.IsNullOrWhiteSpace(ctx.Response.ContentType))
                ctx.Response.ContentType = "text/event-stream";
            TrySetHeader(ctx.Response.Headers, "Cache-Control", "no-cache");
            TrySetHeader(ctx.Response.Headers, "X-Accel-Buffering", "no");
            var buffer = new byte[4096];
            while (true)
            {
                var read = await src.ReadAsync(buffer);
                if (read <= 0) break;
                await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                await ctx.Response.OutputStream.FlushAsync();
            }
            ctx.Response.Close();
            return;
        }

        await src.CopyToAsync(ctx.Response.OutputStream);
        ctx.Response.Close();
    }

    async Task ServeUi(HttpListenerContext ctx, string path)
    {
        if (path == "/") path = "/index.html";
        var ui = AppPaths.UiFolder;
        var full = Path.GetFullPath(Path.Combine(ui, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(ui), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            var index = Path.Combine(ui, "index.html");
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
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
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
        ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };

    static async Task<T?> ReadJson<T>(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var text = await reader.ReadToEndAsync();
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
}
