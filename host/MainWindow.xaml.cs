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

    public void ApplyCaptureProtection()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            // WDA_EXCLUDEFROMCAPTURE paints this window black once WebView2 attaches.
            SetWindowDisplayAffinity(hwnd, WdaNone);
        }
        catch { /* older Windows */ }
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var browser = await WebViewRuntime.EnsureAsync(text => Dispatcher.Invoke(() => BootText.Text = text));
            BootText.Text = "Подготовка интерфейса";
            // Loopback must not go through the system proxy, and GPU compositing
            // leaves this control black on some machines.
            var options = new CoreWebView2EnvironmentOptions(
                "--proxy-bypass-list=<-loopback>;localhost;127.0.0.1 --disable-gpu --disable-gpu-compositing --in-process-gpu --disable-breakpad --renderer-process-limit=1 --disable-features=AudioServiceOutOfProcess,BackForwardCache,TranslateUI,msSmartScreenProtection,SpareRendererForSitePerProcess --disk-cache-size=1 --media-cache-size=1");
            var env = await CoreWebView2Environment.CreateAsync(browser, WebViewRuntime.UserDataFolder, options);
            Web.Visibility = Visibility.Visible;
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
            var navigated = new TaskCompletionSource<bool>();
            void OnNavigated(object? _, CoreWebView2NavigationCompletedEventArgs args)
            {
                var uri = Web.CoreWebView2?.Source ?? "";
                if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
                Web.CoreWebView2!.NavigationCompleted -= OnNavigated;
                navigated.TrySetResult(args.IsSuccess);
            }
            Web.CoreWebView2.NavigationCompleted += OnNavigated;
            Web.Source = new Uri(await ResolveUiUrl());
            var done = await Task.WhenAny(navigated.Task, Task.Delay(TimeSpan.FromSeconds(12)));
            if (done != navigated.Task || !navigated.Task.Result)
                throw new InvalidOperationException("Интерфейс не открылся.");
            Boot.Visibility = Visibility.Collapsed;
            // WebView2 sometimes stays blank until the HWND is resized once.
            var width = Width;
            Width = width + 1;
            Width = width;
        }
        catch (Exception)
        {
            Web.Visibility = Visibility.Collapsed;
            Boot.Visibility = Visibility.Visible;
            BootBar.IsIndeterminate = false;
            BootBar.Visibility = Visibility.Collapsed;
            BootClose.Visibility = Visibility.Visible;
            BootText.Text = "Не удалось открыть интерфейс.\nЗакройте окно и запустите приложение повторно.";
        }
    }

    void OnBootClose(object sender, RoutedEventArgs e) => Close();

    static async Task<string> ResolveUiUrl()
    {
        // Only prefer Vite when explicitly developing — otherwise any local :5173 can hijack the UI.
        var dev = string.Equals(Environment.GetEnvironmentVariable("JESTER_DEV"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("JESTER_DEV"), "true", StringComparison.OrdinalIgnoreCase);
        if (dev)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(350) };
                using var res = await http.GetAsync("http://127.0.0.1:5173");
                if (res.IsSuccessStatusCode) return "http://127.0.0.1:5173/";
            }
            catch { /* packaged ui */ }
        }
        return JesterApi.Origin + "/";
    }
}
