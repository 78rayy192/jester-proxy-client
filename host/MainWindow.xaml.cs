using System.Net.Http;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Text.Json;
using Jester.Host.Services;
using Microsoft.Web.WebView2.Core;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Jester.Host;

public partial class MainWindow : Window
{
    const int WmNclButtonDown = 0xA1;
    const int HtCaption = 0x2;
    const uint WdaNone = 0x0;

    readonly SettingsStore _settings;
    readonly string? _previewUserDataFolder;
    HwndSource? _windowSource;
    bool _materialRefreshQueued;
    (bool Enabled, bool Light, Color Background)? _materialRequest;
    bool _closed;
    bool _uiReady;
    public bool NativeGlassActive { get; private set; }

    public MainWindow(SettingsStore settings) : this(settings, null) { }

    internal MainWindow(SettingsStore settings, string? previewUserDataFolder)
    {
        _settings = settings;
        _previewUserDataFolder = previewUserDataFolder;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(OnNativeMessage);
            ApplyAppearance();
        };
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => UpdateBackgroundMode();
        StateChanged += (_, _) => UpdateBackgroundMode();
        Closed += (_, _) =>
        {
            _closed = true;
            _windowSource?.RemoveHook(OnNativeMessage);
            Web.Dispose();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.GetPosition(this).Y < 8)
                DragMove();
        };
    }

    IntPtr OnNativeMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Native composition/transparency settings can change while the window
        // is open. Reapply once on the dispatcher, without polling or timers.
        if ((message is 0x031E or 0x001A) && !_materialRefreshQueued)
        {
            _materialRequest = null;
            _materialRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _materialRefreshQueued = false;
                if (!_closed) ApplyAppearance();
            }));
        }
        return IntPtr.Zero;
    }

    public void ApplyAppearance()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyAppearance));
            return;
        }
        if (_closed) return;
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        var appearance = _settings.Current.Appearance;
        Color background;
        try { background = (Color)ColorConverter.ConvertFromString(appearance.Background); }
        catch { background = Color.FromRgb(11, 16, 27); }
        var light = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) > 170;
        var request = (appearance.Glass, light, background);
        if (_materialRequest == request)
        {
            SendMaterialState();
            return;
        }
        _materialRequest = request;
        NativeGlassActive = WindowMaterial.Apply(this, appearance.Glass, light);
        WindowFrame.Background = NativeGlassActive ? Brushes.Transparent : new SolidColorBrush(background);
        if (!NativeGlassActive) Background = WindowFrame.Background;
        WindowFrame.BorderBrush = new SolidColorBrush(light ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(45, 231, 242, 255));
        Web.DefaultBackgroundColor = NativeGlassActive
            ? System.Drawing.Color.Transparent
            : System.Drawing.Color.FromArgb(background.R, background.G, background.B);
        SendMaterialState();
    }

    void SendMaterialState()
    {
        if (Web.CoreWebView2 == null) return;
        try { Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "window-material", nativeGlass = NativeGlassActive, materialMode = WindowMaterial.GetMode(this) })); }
        catch (COMException) { /* renderer is closing */ }
        catch (InvalidOperationException) { /* renderer is not ready */ }
    }

    void UpdateBackgroundMode()
    {
        if (!_uiReady || Web.CoreWebView2 == null) return;
        var background = !IsVisible || WindowState == WindowState.Minimized;
        Web.Visibility = background ? Visibility.Hidden : Visibility.Visible;
        try
        {
            Web.CoreWebView2.MemoryUsageTargetLevel = background
                ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (NotImplementedException) { /* older WebView2 runtime */ }
        catch (COMException) { /* runtime does not support memory targets */ }
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
        if (_uiReady) return;
        try
        {
            var browser = await WebViewRuntime.EnsureAsync(text => Dispatcher.Invoke(() => BootText.Text = text));
            BootText.Text = "Подготовка интерфейса";
            // Loopback must not go through the system proxy, and GPU compositing
            // leaves this control black on some machines.
            var arguments = "--proxy-bypass-list=<-loopback>;localhost;127.0.0.1 --disable-gpu --disable-gpu-compositing --in-process-gpu --disable-breakpad --renderer-process-limit=1 --disable-features=AudioServiceOutOfProcess,BackForwardCache,TranslateUI,SpareRendererForSitePerProcess --disk-cache-size=1 --media-cache-size=1";
            if (_previewUserDataFolder != null && Environment.GetEnvironmentVariable("JESTER_PREVIEW_GPU") == "1")
                arguments = arguments.Replace("--disable-gpu --disable-gpu-compositing --in-process-gpu ", "");
            var options = new CoreWebView2EnvironmentOptions(arguments);
            var env = await CoreWebView2Environment.CreateAsync(browser, _previewUserDataFolder ?? WebViewRuntime.UserDataFolder, options);
            Web.Visibility = Visibility.Visible;
            await Web.EnsureCoreWebView2Async(env);
            var stealth = _settings.Current.StealthMode;
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = !stealth;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = !stealth;
            Web.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
                    || !LocalApiPolicy.IsTrustedOrigin(uri.GetLeftPart(UriPartial.Authority), LocalApiPolicy.Development))
                    args.Cancel = true;
            };
            Web.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (args.IsUserInitiated && Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https")
                {
                    try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                    catch { /* no default browser */ }
                }
            };
            Web.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source)
                    || !LocalApiPolicy.IsTrustedOrigin(source.GetLeftPart(UriPartial.Authority), LocalApiPolicy.Development)) return;
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
                        case "material-ready": ApplyAppearance(); break;
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
            Web.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) return;
                _materialRequest = null;
                ApplyAppearance();
            };
            // An isolated native preview must never fall through to the user's live API.
            Web.Source = new Uri(_previewUserDataFolder != null ? "http://127.0.0.1:5173/" : await ResolveUiUrl());
            var done = await Task.WhenAny(navigated.Task, Task.Delay(TimeSpan.FromSeconds(12)));
            if (done != navigated.Task || !navigated.Task.Result)
                throw new InvalidOperationException("Интерфейс не открылся.");
            Boot.Visibility = Visibility.Collapsed;
            _uiReady = true;
            UpdateBackgroundMode();
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
