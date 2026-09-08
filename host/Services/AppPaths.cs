using System.Security.Principal;

namespace Jester.Host.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jester");

    public static string Cores => Path.Combine(Root, "cores");
    public static string Run => Path.Combine(Root, "run");
    public static string Profiles => Path.Combine(Root, "profiles");
    public static string Logs => Path.Combine(Root, "logs");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string MihomoExe => Path.Combine(Cores, "mihomo.exe");
    public static string WintunDll => Path.Combine(Cores, "wintun.dll");
    public static string RuntimeConfig => Path.Combine(Run, "config.yaml");
    public static string CoreLog => Path.Combine(Logs, "core.log");
    public static string UiFolder => Path.Combine(AppContext.BaseDirectory, "ui");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cores);
        Directory.CreateDirectory(Run);
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Logs);
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
