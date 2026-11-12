using System.Reflection;
using Jester.Host.Models;
using Jester.Host.Services;

namespace Jester.Regression;

internal static class ProfileRegression
{
    const string Id = "abcdef123456";

    public static void Register(RegressionSuite suite)
    {
        var read = typeof(ProfileStore).GetMethod("ReadMetadata", BindingFlags.Static | BindingFlags.NonPublic)!;
        ProfileInfo? Read(string id, string json) => (ProfileInfo?)read.Invoke(null, [id, json]);

        suite.Case("Corrupt and mismatched profile metadata is ignored", () =>
        {
            Check.True(Read(Id, "{broken") == null, "Corrupt JSON was returned");
            Check.True(Read(Id, "{\"id\":\"123456abcdef\"}") == null, "Mismatched ID was returned");
            Check.True(Read(Id, "{\"name\":\"Missing ID\"}") == null, "Missing ID was returned");
            Check.True(Read(Id, "{\"id\":\"../../secret\"}") == null, "Unsafe ID was returned");
        });
        suite.Case("Nullable profile metadata fields recover without UI errors", () =>
        {
            var metadata = Read(Id, "{\"id\":\"abcdef123456\",\"name\":null,\"source\":null,\"kind\":null}")!;
            Check.Equal("Профиль", metadata.Name);
            Check.Equal("", metadata.Source);
            Check.Equal("paste", metadata.Kind);
        });
        suite.Case("Profile IDs require exactly twelve lowercase hexadecimal characters", () =>
        {
            Check.True(ProfileStore.IsValidId(Id), "Valid ID rejected");
            foreach (var value in new[] { Id + "\n", "../" + Id, "ABCDEF123456", Id[..^1], "abcdef12345z" })
                Check.True(!ProfileStore.IsValidId(value), "Unsafe ID accepted");
        });
        suite.Case("Subscription buffering is limited to eight MiB", () =>
        {
            var http = (System.Net.Http.HttpClient)typeof(ProfileStore).GetField("Http", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Check.Equal(8L * 1024 * 1024, http.MaxResponseContentBufferSize);
        });
        suite.Case("Canceled subscription update exits before reading profile files", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Check.Throws<OperationCanceledException>(() => new ProfileStore().UpdateSubscriptionAsync(Id, cancellation.Token).GetAwaiter().GetResult());
        });
    }
}
