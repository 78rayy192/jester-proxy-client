using System.IO.Compression;
using System.Reflection;
using System.Text;
using Jester.Host.Services;
using Jester.Host.Models;

namespace Jester.Regression;

internal static class CoreRegression
{
    sealed class BlockedUiContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
    }
    static T Field<T>(CoreManager core, string name) => (T)typeof(CoreManager)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(core)!;

    public static void Register(RegressionSuite suite)
    {
        suite.Case("A cancelled start does not replace connection intent", () =>
        {
            var core = new CoreManager(new SettingsStore(), new ProfileStore(), new CoreDownloader());
            Check.Throws<OperationCanceledException>(() => core.StartAsync(new CancellationToken(true)).GetAwaiter().GetResult());
            Check.True(Field<CancellationTokenSource?>(core, "_runIntent") == null, "Cancelled request changed active intent");
        });

        suite.Case("A newer start cancels the previous queued connection attempt", () =>
        {
            var core = new CoreManager(new SettingsStore(), new ProfileStore(), new CoreDownloader());
            var lifecycle = Field<SemaphoreSlim>(core, "_lifecycle");
            lifecycle.Wait(); // Keep both attempts away from real files and network state.
            using var cancelSecond = new CancellationTokenSource();
            try
            {
                var first = core.StartAsync();
                Check.True(!first.IsCompleted, "First start did not wait for lifecycle lock");
                var second = core.StartAsync(cancelSecond.Token);
                Check.Throws<OperationCanceledException>(() => first.GetAwaiter().GetResult());
                Check.True(!second.IsCompleted, "Second start unexpectedly acquired lifecycle lock");
                cancelSecond.Cancel();
                Check.Throws<OperationCanceledException>(() => second.GetAwaiter().GetResult());
            }
            finally
            {
                cancelSecond.Cancel();
                Field<CancellationTokenSource>(core, "_runIntent").Dispose();
                lifecycle.Release();
                lifecycle.Dispose();
            }
        });

        suite.Case("Queued startup cancellation does not require a UI continuation", () =>
        {
            var core = new CoreManager(new SettingsStore(), new ProfileStore(), new CoreDownloader());
            var lifecycle = Field<SemaphoreSlim>(core, "_lifecycle");
            lifecycle.Wait(); // No profile files, processes, or system proxy can be touched.
            var previous = SynchronizationContext.Current;
            var blockedUi = new BlockedUiContext();
            using var cancellation = new CancellationTokenSource();
            try
            {
                SynchronizationContext.SetSynchronizationContext(blockedUi);
                var request = core.StartAsync(cancellation.Token);
                Check.True(!request.IsCompleted, "Startup did not wait for the lifecycle lock");
                cancellation.Cancel();
                var finished = Task.WhenAny(request, Task.Delay(1500)).GetAwaiter().GetResult();
                Check.True(ReferenceEquals(request, finished), "Cancellation deadlocked on the blocked UI context");
                Check.Throws<OperationCanceledException>(() => request.GetAwaiter().GetResult());
                Check.Equal(0, blockedUi.Posts, "Startup posted a continuation to the UI thread");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                cancellation.Cancel();
                Field<CancellationTokenSource>(core, "_runIntent").Dispose();
                lifecycle.Release();
            }
        });

        suite.Case("Log polling reads a bounded tail from a large UTF-8 journal", () =>
        {
            using var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, leaveOpen: true))
            {
                writer.Write(new string('я', 300_000));
                writer.Write("\nold\nпоследняя 1\nпоследняя 2");
            }
            stream.Position = 0;
            var method = typeof(CoreManager).GetMethod("ReadLogTail", BindingFlags.Static | BindingFlags.NonPublic)!;
            var text = (string)method.Invoke(null, [stream, 2])!;
            Check.Equal("последняя 1\nпоследняя 2", text);
            Check.True(stream.CanRead, "Tail reader closed the caller's stream");
        });

        suite.Case("Invalid downloaded executable preserves the installed binary", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "jester-binary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var destination = Path.Combine(directory, "mihomo.exe");
                File.WriteAllText(destination, "known-good-installed-binary");
                var archivePath = Path.Combine(directory, "test.zip");
                using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("mihomo.exe").Open()))
                    writer.Write("invalid executable");
                using var archive = ZipFile.OpenRead(archivePath);
                var method = typeof(CoreDownloader).GetMethod("ExtractBinary", BindingFlags.Static | BindingFlags.NonPublic)!;
                try { method.Invoke(null, [archive.Entries[0], destination]); throw new InvalidOperationException("Expected invalid executable rejection"); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
                Check.Equal("known-good-installed-binary", File.ReadAllText(destination));
                Check.True(!File.Exists(destination + ".new"), "Partial executable was not cleaned up");
            }
            finally { Directory.Delete(directory, true); }
        });

        suite.Case("Invalid proxy port is rejected before changing Windows settings", () =>
        {
            Check.Throws<ArgumentOutOfRangeException>(() => SystemProxy.Set(true, 0));
            Check.Throws<ArgumentOutOfRangeException>(() => SystemProxy.Set(true, 65536));
        });

        suite.Case("Leak policy does not claim QUIC or STUN blocking outside rule mode", () =>
        {
            var method = typeof(CoreManager).GetMethod("BuildShieldChecks", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var mode in new[] { "global", "direct", "rule" })
            {
                var settings = new AppSettings { Mode = mode, StealthMode = true, BlockQuic = false, BlockStun = false };
                var checks = (List<GhostCheck>)method.Invoke(null, [settings])!;
                Check.Equal(mode == "rule", checks.Single(c => c.Id == "quic").Ok);
                Check.Equal(mode == "rule", checks.Single(c => c.Id == "stun").Ok);
            }
        });
    }
}
