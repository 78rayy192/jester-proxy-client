using System.Text.RegularExpressions;

namespace Jester.Host.Services;

public static class DeviceId
{
    static readonly Regex Valid = new("^[a-zA-Z0-9=-]{10,64}$", RegexOptions.Compiled);

    public static string Current()
    {
        AppPaths.Ensure();
        var path = Path.Combine(AppPaths.Root, "device-id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Valid.IsMatch(existing)) return existing;
        }

        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, id);
        return id;
    }
}
