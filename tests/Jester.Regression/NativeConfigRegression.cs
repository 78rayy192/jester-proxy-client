using System.Diagnostics;
using System.Text;
using Jester.Host.Models;
using Jester.Host.Services;

namespace Jester.Regression;

/// <summary>Opt-in parsing by an installed core. Never starts listeners or reads a user profile.</summary>
internal static class NativeConfigRegression
{
    public static void Register(RegressionSuite suite)
    {
        var executable = Environment.GetEnvironmentVariable("JESTER_NATIVE_CORE");
        var geoip = Environment.GetEnvironmentVariable("JESTER_NATIVE_GEOIP");
        if (string.IsNullOrWhiteSpace(executable)) return;
        Check.True(File.Exists(executable), "JESTER_NATIVE_CORE must point to an existing Mihomo executable");
        Check.True(!string.IsNullOrWhiteSpace(geoip) && File.Exists(geoip), "Set JESTER_NATIVE_GEOIP to an existing geoip.metadb to prevent downloads");

        const string uuid = "11111111-1111-4111-8111-111111111111";
        (string Name, string Profile)[] fixtures =
        [
            ("VLESS", $"vless://{uuid}@127.0.0.1:9?security=tls&sni=example.invalid&type=ws&path=%2Ffixture#NativeVless"),
            ("Trojan", "trojan://fixture-password@127.0.0.1:9?sni=example.invalid&type=ws&path=%2Ffixture#NativeTrojan"),
            ("Hysteria2", "hysteria2://fixture-password@127.0.0.1:9?sni=example.invalid#NativeHysteria"),
            ("Shadowsocks", "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-128-gcm:fixture-password")) + "@127.0.0.1:9#NativeSS"),
            ("TUIC", $"tuic://{uuid}:fixture-password@127.0.0.1:9?sni=example.invalid#NativeTuic")
        ];
        foreach (var (name, profile) in fixtures)
            suite.Case("Native Mihomo accepts generated " + name + " config", () => Validate(executable, geoip!, profile));
    }

    static void Validate(string executable, string geoip, string profile)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", "native-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "ruleset"));
        // The generated endpoints are loopback discard ports. Only --test is used;
        // TUN, Windows proxy settings, listeners, and runtime connectivity stay untouched.
        var settings = new AppSettings { RefilterEnabled = false };
        var config = ConfigBuilder.Merge(profile, settings, "synthetic-validation-secret");
        var configPath = Path.Combine(directory, "config.yaml");
        File.WriteAllText(configPath, config);
        File.WriteAllText(Path.Combine(directory, "ruleset", RuleListService.CustomDirectFile), "example.invalid\n");
        File.WriteAllText(Path.Combine(directory, "ruleset", RuleListService.CustomProxyFile), "proxy.example.invalid\n");
        File.Copy(geoip, Path.Combine(directory, "geoip.metadb"));

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add(configPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Mihomo validation did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { process.WaitForExitAsync(cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Mihomo --test timed out. Fixture: " + directory);
        }
        var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Check.True(process.ExitCode == 0, "Mihomo rejected generated configuration: " + output + " Fixture: " + directory);
    }
}
