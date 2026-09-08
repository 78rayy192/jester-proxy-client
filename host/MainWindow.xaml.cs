using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Jester.Host.Services;
using Microsoft.Web.WebView2.Core;

namespace Jester.Host;

public partial class MainWindow : Window
{
    const int WmNclButtonDown = 0xA1;
    const int HtCaption = 0x2;
    const uint WdaNone = 0x0;
    const uint WdaExcludeFromCapture = 0x11;

    readonly SettingsStore _settings;

    public MainWindow(SettingsStore settings)
    {
        _settings = settings;
        InitializeComponent();
        Loaded += OnLoaded;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.GetPosition(this).Y < 8)
                DragMove();
        };
    }

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    void BeginWindowDrag()
    {
        try
        {
            ReleaseCapture();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SendMessage(hwnd, WmNclButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
            else if (Mouse.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }
        catch { /* ignore */ }
    }

    void ApplyCaptureProtection()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var hide = _settings.Current.HideFromCapture || _settings.Current.StealthMode;
            SetWindowDisplayAffinity(hwnd, hide ? WdaExcludeFromCapture : WdaNone);
        }
        catch { /* older Windows */ }
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var env = await CoreWebView2Environment.CreateAsync();
        await Web.EnsureCoreWebView2Async(env);
        var stealth = _settings.Current.StealthMode;
        Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = !stealth;
        Web.CoreWebView2.Settings.AreDevToolsEnabled = !stealth;
        Web.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            var msg = args.TryGetWebMessageAsString();
            Dispatcher.Invoke(() =>
            {
                switch (msg)
                {
                    case "drag":
                        BeginWindowDrag();
                        break;
                    case "minimize": WindowActions.Minimize?.Invoke(); break;
                    case "maximize": WindowActions.ToggleMaximize?.Invoke(); break;
                    case "close": WindowActions.CloseOrTray?.Invoke(_settings.Current.CloseToTray); break;
                }
            });
        };

        ApplyCaptureProtection();
        Web.Source = new Uri(await ResolveUiUrl());
    }

    static async Task<string> ResolveUiUrl()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(350) };
            using var res = await http.GetAsync("http://127.0.0.1:5173");
            if (res.IsSuccessStatusCode) return "http://127.0.0.1:5173/";
        }
        catch { /* packaged ui */ }
        return JesterApi.Origin + "/";
    }
}
