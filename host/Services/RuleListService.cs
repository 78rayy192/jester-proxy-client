using System.Net.Http;
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

    public static string RulesDir => Path.Combine(AppPaths.Run, "ruleset");

    public static async Task EnsureAsync(AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(RulesDir);
        WriteText(CustomProxyFile, settings.CustomProxyRules);
        WriteText(CustomDirectFile, settings.CustomDirectRules);
        if (!settings.RefilterEnabled) return;

        if (settings.UseMrsRules)
        {
            var dMrs = Path.Combine(RulesDir, DomainsMrsFile);
            var iMrs = Path.Combine(RulesDir, IpsMrsFile);
            if (IsStale(dMrs) || !File.Exists(dMrs))
                await DownloadAsync(settings.RefilterDomainsMrsUrl, dMrs, ct);
            if (settings.RefilterUseIp && (IsStale(iMrs) || !File.Exists(iMrs)))
                await DownloadAsync(settings.RefilterIpMrsUrl, iMrs, ct);
            return;
        }

        var domains = Path.Combine(RulesDir, DomainsFile);
        var ips = Path.Combine(RulesDir, IpsFile);
        if (IsStale(domains) || !File.Exists(domains))
            await DownloadAsync(settings.RefilterDomainsUrl, domains, ct);
        if (settings.RefilterUseIp && (IsStale(ips) || !File.Exists(ips)))
            await DownloadAsync(settings.RefilterIpUrl, ips, ct);
    }

    public static async Task<RuleListStatus> UpdateAsync(AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(RulesDir);
        if (settings.UseMrsRules)
        {
            await DownloadAsync(settings.RefilterDomainsMrsUrl, Path.Combine(RulesDir, DomainsMrsFile), ct);
            if (settings.RefilterUseIp)
                await DownloadAsync(settings.RefilterIpMrsUrl, Path.Combine(RulesDir, IpsMrsFile), ct);
        }
        else
        {
            await DownloadAsync(settings.RefilterDomainsUrl, Path.Combine(RulesDir, DomainsFile), ct);
            if (settings.RefilterUseIp)
                await DownloadAsync(settings.RefilterIpUrl, Path.Combine(RulesDir, IpsFile), ct);
        }
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

    static async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var tmp = dest + ".tmp";
        await using (var fs = File.Create(tmp))
            await res.Content.CopyToAsync(fs, ct);
        File.Copy(tmp, dest, true);
        File.Delete(tmp);
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
        File.WriteAllLines(path, lines);
    }

    static bool IsStale(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 32) return true;
        return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(20);
    }

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
    public string Message { get; set; } = "";
}
