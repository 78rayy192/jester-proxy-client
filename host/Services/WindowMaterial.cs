using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace Jester.Host.Services;

/// <summary>
/// One native Desktop Acrylic surface, composed by DWM. This never turns the
/// window into a layered WPF window and never applies opacity to its contents.
/// </summary>
public static class WindowMaterial
{
    const int DwmUseImmersiveDarkMode = 20;
    const int DwmWindowCornerPreference = 33;
    const int DwmSystemBackdropType = 38;
    const int BackdropNone = 1;
    const int BackdropDesktopAcrylic = 3;
    static readonly Lazy<CompositionSetter?> SetComposition = new(LoadCompositionSetter);
    static readonly ConditionalWeakTable<Window, MaterialState> States = new();

    // Opt-in native QA can compare the two app-local fallbacks without changing
    // any global Windows preferences. Production uses the documented material first.
    internal static int PreviewAccentMode { get; set; }
    public static string GetMode(Window window) => States.TryGetValue(window, out var state) ? state.Mode : "solid";

    public static bool Apply(Window window, bool enabled, bool light)
    {
        window.Dispatcher.VerifyAccess();
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        var source = HwndSource.FromHwnd(hwnd);
        var fallback = light ? Color.FromRgb(234, 240, 249) : Color.FromRgb(11, 16, 27);
        if (enabled)
        {
            // WindowChrome can extend/reset the native frame while its property
            // changes. Prepare the WPF surface first, then commit the HWND policy.
            SetGlassFrame(window, true);
            window.Background = Brushes.Transparent;
            if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        var applied = false;
        var mode = "solid";
        try
        {
            var dark = light ? 0 : 1;
            var rounded = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref rounded, sizeof(int));
            SetAccent(hwnd, 0, light);

            var compose = enabled && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)
                && !SystemParameters.HighContrast && DwmIsCompositionEnabled(out var composition) >= 0 && composition;
            if (compose && PreviewAccentMode is not (3 or 4)
                && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && TransparencyEnabled())
            {
                var backdrop = BackdropDesktopAcrylic;
                var margins = new Margins(-1);
                applied = DwmExtendFrameIntoClientArea(hwnd, ref margins) >= 0
                    && DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref backdrop, sizeof(int)) >= 0;
                if (applied) mode = "desktop-acrylic";
            }
            if (!applied && compose)
            {
                // Desktop Acrylic can be suppressed by the OS transparency preference.
                // The explicit application Glass preference uses an HWND-local blur
                // policy instead; no registry or global Windows setting is written.
                var backdrop = BackdropNone;
                DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref backdrop, sizeof(int));
                var margins = new Margins(-1);
                if (DwmExtendFrameIntoClientArea(hwnd, ref margins) >= 0)
                {
                    var accent = PreviewAccentMode == 4 ? 4 : 3;
                    applied = SetAccent(hwnd, accent, light);
                    if (!applied && accent == 3) { accent = 4; applied = SetAccent(hwnd, accent, light); }
                    if (applied) mode = accent == 3 ? "accent-blur" : "accent-acrylic";
                }
            }
            if (!applied)
            {
                var backdrop = BackdropNone;
                var margins = new Margins(0);
                DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref backdrop, sizeof(int));
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
                SetAccent(hwnd, 0, light);
            }
        }
        catch (DllNotFoundException) { applied = false; }
        catch (EntryPointNotFoundException) { applied = false; }
        catch (COMException) { applied = false; }

        if (!applied) SetGlassFrame(window, false);
        window.Background = applied ? Brushes.Transparent : new SolidColorBrush(fallback);
        if (source?.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = applied ? Colors.Transparent : fallback;
        States.GetValue(window, static _ => new MaterialState()).Mode = applied ? mode : "solid";
        return applied;
    }

    static void SetGlassFrame(Window window, bool enabled)
    {
        var chrome = WindowChrome.GetWindowChrome(window);
        if (chrome == null) return;
        if (chrome.IsFrozen)
        {
            chrome = (WindowChrome)chrome.Clone();
            WindowChrome.SetWindowChrome(window, chrome);
        }
        chrome.GlassFrameThickness = enabled ? new Thickness(-1) : new Thickness(0);
    }

    static bool SetAccent(IntPtr hwnd, int accent, bool light)
    {
        var setter = SetComposition.Value;
        if (setter == null) return false;
        var policy = new AccentPolicy
        {
            State = accent,
            Flags = accent == 4 ? 2u : 0u,
            // AABBGGRR; tint only the background, never the complete window.
            GradientColor = accent == 4 ? (light ? 0x20FFFFFFu : 0x301B100Bu) : 0u
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new CompositionAttributeData { Attribute = 19, Data = pointer, Size = (uint)size };
            return setter(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    static CompositionSetter? LoadCompositionSetter()
    {
        // Microsoft's SWCA contract requires resolving the optional export at runtime.
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "user32.dll"), out var library)) return null;
        if (NativeLibrary.TryGetExport(library, "SetWindowCompositionAttribute", out var address))
            return Marshal.GetDelegateForFunctionPointer<CompositionSetter>(address);
        NativeLibrary.Free(library);
        return null;
    }

    sealed class MaterialState { public string Mode = "solid"; }

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy { public int State; public uint Flags; public uint GradientColor; public uint AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    struct CompositionAttributeData { public int Attribute; public IntPtr Data; public uint Size; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    delegate bool CompositionSetter(IntPtr hwnd, ref CompositionAttributeData data);

    static bool TransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
        public Margins(int value) => Left = Right = Top = Bottom = value;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")]
    static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}
