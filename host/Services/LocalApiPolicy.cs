namespace Jester.Host.Services;

internal static class LocalApiPolicy
{
    internal static bool IsAllowed(string? origin, string? fetchSite, string? authority, bool development)
    {
        if (!string.Equals(authority, $"127.0.0.1:{JesterApi.Port}", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(origin))
            return IsTrustedOrigin(origin, development);
        return !string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsTrustedOrigin(string origin, bool development) =>
        string.Equals(origin, JesterApi.Origin, StringComparison.OrdinalIgnoreCase)
        || (development && (origin == "http://127.0.0.1:5173" || origin == "http://localhost:5173"));

    internal static bool Development => Environment.GetEnvironmentVariable("JESTER_DEV") is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
