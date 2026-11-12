using System.Text;
using Jester.Host.Models;
using Jester.Host.Services;

namespace Jester.Regression;

internal static class ApiRegression
{
    public static void Register(RegressionSuite suite)
    {
        suite.Case("Local API rejects foreign, null, and development origins in production", () =>
        {
            foreach (var origin in new[] { "https://example.com", "null", "http://127.0.0.1:5173", JesterApi.Origin + ".evil.test" })
                Check.True(!LocalApiPolicy.IsAllowed(origin, "cross-site", "127.0.0.1:37841", false), "Untrusted origin accepted: " + origin);
            Check.True(!LocalApiPolicy.IsAllowed(null, "cross-site", "127.0.0.1:37841", false), "Cross-site form accepted");
            Check.True(!LocalApiPolicy.IsAllowed(null, null, "evil.test:37841", false), "DNS rebinding Host accepted");
        });
        suite.Case("Local API allows same-origin and explicitly enabled development traffic", () =>
        {
            Check.True(LocalApiPolicy.IsAllowed(JesterApi.Origin, "same-origin", "127.0.0.1:37841", false), "Packaged UI blocked");
            Check.True(LocalApiPolicy.IsAllowed(null, null, "127.0.0.1:37841", false), "Local tools blocked");
            Check.True(LocalApiPolicy.IsAllowed("http://localhost:5173", "same-site", "127.0.0.1:37841", true), "Opt-in development blocked");
        });
        suite.Case("Mihomo JSON lines become complete SSE messages", () =>
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"up\":12,\"down\":34}\r\n\n{\"inuse\":1024}\n"));
            using var output = new MemoryStream();
            MihomoStream.WriteSseAsync(input, output, CancellationToken.None).GetAwaiter().GetResult();
            Check.Equal("data: {\"up\":12,\"down\":34}\n\ndata: {\"inuse\":1024}\n\n", Encoding.UTF8.GetString(output.ToArray()));
        });
        suite.Case("Stream shutdown propagates cancellation", () =>
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("{}\n"));
            using var output = new MemoryStream();
            Check.Throws<OperationCanceledException>(() => MihomoStream.WriteSseAsync(input, output, new CancellationToken(true)).GetAwaiter().GetResult());
        });
        suite.Case("Settings reject colliding and reserved ports", () =>
        {
            Check.Throws<ArgumentException>(() => SettingsStore.Normalize(new() { MixedPort = 0 }));
            Check.Throws<ArgumentException>(() => SettingsStore.Normalize(new() { MixedPort = JesterApi.Port }));
            Check.Throws<ArgumentException>(() => SettingsStore.Normalize(new() { ExternalPort = 7890 }));
        });
        suite.Case("Imported null settings and extreme values are normalized", () =>
        {
            var settings = SettingsStore.Normalize(new() { Dns = null!, Appearance = null!, FavoriteNodes = null!, Mtu = -10,
                LatencyTimeoutMs = int.MaxValue, AdaptiveLaneIntervalSec = -1, Mode = "invalid" });
            Check.True(settings.Dns != null && settings.Appearance != null && settings.FavoriteNodes != null, "Null collections survived import");
            Check.Equal(1280, settings.Mtu);
            Check.Equal(30000, settings.LatencyTimeoutMs);
            Check.Equal(15, settings.AdaptiveLaneIntervalSec);
            Check.Equal("rule", settings.Mode);
        });
        suite.Case("Failed settings mutation does not alter the current snapshot", () =>
        {
            var store = new SettingsStore();
            Check.Throws<InvalidOperationException>(() => store.Patch(s => { s.Mtu = 9000; throw new InvalidOperationException("invalid edit"); }));
            Check.Equal(1400, store.Current.Mtu);
        });
        suite.Case("Atomic settings replacement leaves no partial files", () =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", "atomic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "settings.json");
                AtomicFile.WriteAllText(path, "old");
                AtomicFile.WriteAllText(path, "новые данные");
                Check.Equal("новые данные", File.ReadAllText(path));
                Check.Equal(1, Directory.GetFiles(directory).Length);
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
    }
}
