using System.Net.Http;
using System.Collections.Concurrent;
using Jester.Host.Models;

namespace Jester.Host.Services;

public sealed class RuleListService
{
    public const string DomainsFile = "refilter-domains.txt";
    public const string IpsFile = "refilter-ipsum.txt";
    public const string DomainsMrsFile = "refilter-domains.mrs";
    public const string IpsMrsFile = "refilter-ipsum.mrs";
    public const string CustomProxyFile = "custom-proxy.txt";
    public const string CustomDirectFile = "custom-direct.txt";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Downloads = new(StringComparer.OrdinalIgnoreCase);

    public static string RulesDir => Path.Combine(AppPaths.Run, "ruleset");

    public static async Task EnsureAsync(AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(RulesDir);
        WriteText(CustomProxyFile, settings.CustomProxyRules);
        WriteText(CustomDirectFile, settings.CustomDirectRules);
        if (!settings.RefilterEnabled) return;

        // Mihomo's HTTP providers refresh cached lists themselves. Reuse a valid
        // cache immediately; a stale remote source must not stall every reconnect.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        await Task.WhenAll(RequiredLists(settings)
            .Where(list => !UsableCache(list.Path))
            .Select(list => DownloadAsync(list.Url, list.Path, budget.Token, onlyMissing: true)));
    }

    public static async Task<RuleListStatus> UpdateAsync(AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(RulesDir);
        await Task.WhenAll(RequiredLists(settings).Select(list => DownloadAsync(list.Url, list.Path, ct)));
        WriteText(CustomProxyFile, settings.CustomProxyRules);
        WriteText(CustomDirectFile, settings.CustomDirectRules);
        return Status(settings);
    }

    public static RuleListStatus Status(AppSettings? settings = null)
    {
        var useMrs = settings?.UseMrsRules ?? true;
        var domains = Path.Combine(RulesDir, useMrs ? DomainsMrsFile : DomainsFile);
        var ips = Path.Combine(RulesDir, useMrs ? IpsMrsFile : IpsFile);
        return new RuleListStatus
        {
            DomainsPath = domains,
            IpsPath = ips,
            Format = useMrs ? "mrs" : "text",
            DomainsCount = useMrs ? (File.Exists(domains) ? (int)(new FileInfo(domains).Length / 32) : 0) : CountLines(domains),
            IpsCount = useMrs ? (File.Exists(ips) ? (int)(new FileInfo(ips).Length / 16) : 0) : CountLines(ips),
            DomainsBytes = File.Exists(domains) ? new FileInfo(domains).Length : 0,
            IpsBytes = File.Exists(ips) ? new FileInfo(ips).Length : 0,
            DomainsUpdatedAt = File.Exists(domains) ? File.GetLastWriteTimeUtc(domains) : null,
            IpsUpdatedAt = File.Exists(ips) ? File.GetLastWriteTimeUtc(ips) : null
        };
    }

    static IEnumerable<(string Url, string Path)> RequiredLists(AppSettings settings)
    {
        yield return settings.UseMrsRules
            ? (settings.RefilterDomainsMrsUrl, Path.Combine(RulesDir, DomainsMrsFile))
            : (settings.RefilterDomainsUrl, Path.Combine(RulesDir, DomainsFile));
        if (settings.RefilterUseIp)
            yield return settings.UseMrsRules
                ? (settings.RefilterIpMrsUrl, Path.Combine(RulesDir, IpsMrsFile))
                : (settings.RefilterIpUrl, Path.Combine(RulesDir, IpsFile));
    }

    static async Task DownloadAsync(string url, string dest, CancellationToken ct, bool onlyMissing = false)
    {
        var gate = Downloads.GetOrAdd(dest, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        var tmp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (onlyMissing && UsableCache(dest)) return;
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            await using (var fs = File.Create(tmp))
                await res.Content.CopyToAsync(fs, ct);
            if (!UsableCache(tmp)) throw new InvalidDataException("Получен пустой список правил.");
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, dest, true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            gate.Release();
        }
    }

    static void WriteText(string name, string content)
    {
        var path = Path.Combine(RulesDir, name);
        var lines = (content ?? "")
            .Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (lines.Count == 0) lines.Add("# empty");
        var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, true);
        }
        finally { try { File.Delete(temporary); } catch { /* best effort */ } }
    }

    static bool UsableCache(string path) => File.Exists(path) && new FileInfo(path).Length >= 32;

    static int CountLines(string path)
    {
        if (!File.Exists(path)) return 0;
        var n = 0;
        foreach (var line in File.ReadLines(path))
        {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith('#')) continue;
            n++;
        }
        return n;
    }
}

public sealed class RuleListStatus
{
    public string DomainsPath { get; set; } = "";
    public string IpsPath { get; set; } = "";
    public string Format { get; set; } = "text";
    public int DomainsCount { get; set; }
    public int IpsCount { get; set; }
    public long DomainsBytes { get; set; }
    public long IpsBytes { get; set; }
    public DateTime? DomainsUpdatedAt { get; set; }
    public DateTime? IpsUpdatedAt { get; set; }
}

public sealed class LeakCheckResult
{
    public string? RealIp { get; set; }
    public string? ExitIp { get; set; }
    public bool Masked { get; set; }
    public bool Running { get; set; }
    public bool TunMode { get; set; }
    public int Score { get; set; }
    public string Message { get; set; } = "";
    public List<GhostCheck> Checks { get; set; } = [];
}

public sealed class GhostCheck
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Ok { get; set; }
    public int Weight { get; set; } = 1;
    public string Detail { get; set; } = "";
}

public sealed class GhostPanicResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
}

public sealed class RushResult
{
    public bool Enabled { get; set; }
    public string Lane { get; set; } = "";
    public string? Node { get; set; }
    public int DelayMs { get; set; }
    public string Message { get; set; } = "";
}

public sealed class LearnResult
{
    public bool Ok { get; set; }
    public int Added { get; set; }
    public List<string> Domains { get; set; } = [];
    public string Message { get; set; } = "";
}

public sealed class PortApplyResult
{
    public bool Ok { get; set; }
    public int MixedPort { get; set; }
    public int SocksPort { get; set; }
    public int ExternalPort { get; set; }
    public string Message { get; set; } = "";
}
