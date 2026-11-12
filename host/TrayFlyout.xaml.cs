using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Jester.Host.Services;
using WpfColor = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Jester.Host;

/// <summary>A native, short-lived surface. No browser, polling, or animated blur.</summary>
public partial class TrayFlyout : Window
{
    readonly TrayHost _host;
    readonly Action<string, bool>? _notify;
    bool _busy;
    bool _closing;
    string? _appearanceKey;
    IntPtr _materialHandle;
    public bool NativeMaterialActive { get; private set; }
    internal bool KeepOpenForPreview { get; set; }

    public TrayFlyout(TrayHost host, Action<string, bool>? notify = null)
    {
        _host = host;
        _notify = notify;
        InitializeComponent();
        SourceInitialized += (_, _) => RefreshState(forceMaterial: true);
        Deactivated += (_, _) => { if (!_closing && !KeepOpenForPreview) Hide(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        Closed += (_, _) => _closing = true;
        RefreshState();
    }

    public void RefreshState(bool forceMaterial = false)
    {
        if (_closing) return;
        var settings = _host.Settings();
        var running = _host.Running();
        var appearance = settings.Appearance;
        var light = IsLight(appearance.Background);
        var materialHandle = new WindowInteropHelper(this).Handle;
        var appearanceKey = $"{appearance.Glass}|{appearance.Background}|{appearance.Accent}";
        if (forceMaterial || _materialHandle != materialHandle || _appearanceKey != appearanceKey)
        {
            _materialHandle = materialHandle;
            _appearanceKey = appearanceKey;
            NativeMaterialActive = WindowMaterial.Apply(this, appearance.Glass, light);
            SetBrush("FlyoutText", light ? "#17243B" : "#F2F6FF");
            SetBrush("FlyoutMuted", light ? "#637189" : "#ADBCD3");
            SetBrush("FlyoutEdge", light ? "#75FFFFFF" : "#30FFFFFF");
            SetBrush("FlyoutHover", light ? "#1E366CEB" : "#16FFFFFF");
            SetBrush("FlyoutSelected", light ? "#25376BEA" : "#27B8D4FF");
            SetBrush("FlyoutAccent", appearance.Accent);
            SetBrush("FlyoutDanger", light ? "#A52F4B" : "#FFB3C2");
            Shell.Background = NativeMaterialActive
                ? new LinearGradientBrush(light ? WpfColor.FromArgb(72, 255, 255, 255) : WpfColor.FromArgb(44, 219, 235, 255),
                    light ? WpfColor.FromArgb(26, 234, 240, 249) : WpfColor.FromArgb(18, 11, 16, 27), 125)
                : new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(light ? "#F0F4FB" : "#162031"));
            StatusPanel.Background = new SolidColorBrush(light ? WpfColor.FromArgb(26, 255, 255, 255) : WpfColor.FromArgb(11, 255, 255, 255));
        }
        StatusLabel.Text = running ? "Подключено" : "Не подключено";
        StatusDot.Fill = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(running ? (light ? "#26846D" : "#9BE6CF") : (light ? "#8794A6" : "#8492A6")));
        var selected = _host.SelectedNode?.Invoke();
        if (string.IsNullOrWhiteSpace(selected)) selected = settings.LastSelectedNode;
        ServerLabel.Text = string.IsNullOrWhiteSpace(selected) ? "Сервер не выбран" : selected;
        ServerLabel.ToolTip = ServerLabel.Text;
        RunLabel.Text = _busy ? "Подождите…" : running ? "Отключить" : "Подключить";
        TunChoice.IsChecked = settings.TunEnabled;
        ProxyChoice.IsChecked = settings.SystemProxy && !settings.TunEnabled;
        RuleChoice.IsChecked = settings.Mode == "rule";
        GlobalChoice.IsChecked = settings.Mode == "global";
        DirectChoice.IsChecked = settings.Mode == "direct";
        PortLabel.Text = $"127.0.0.1:{settings.MixedPort}";
        ActionsRoot.IsEnabled = !_busy;
        RestartButton.IsEnabled = running;
    }

    public void ShowAtCursor()
    {
        if (_closing) return;
        if (IsVisible) { Hide(); return; }
        RefreshState(forceMaterial: true);
        var cursor = System.Windows.Forms.Cursor.Position;
        var work = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        // Move before showing so Windows selects the monitor's DPI for layout.
        SetWindowPos(handle, IntPtr.Zero, Math.Clamp(cursor.X - 338, work.Left, Math.Max(work.Left, work.Right - 338)),
            Math.Max(work.Top, cursor.Y - 540), 0, 0, 0x0015);
        var dpi = VisualTreeHelper.GetDpi(this);
        MaxHeight = Math.Max(240, work.Height / dpi.DpiScaleY - 18);
        Scroller.MaxHeight = MaxHeight - 20;
        UpdateLayout();
        Show();
        UpdateLayout();
        dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var x = Math.Clamp(cursor.X - width + 8, work.Left + 8, Math.Max(work.Left + 8, work.Right - width - 8));
        var y = Math.Clamp(cursor.Y - height - 8, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - height - 8));
        SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x0014);
        Activate();
        SetForegroundWindow(handle);
        RunButton.Focus();
    }

    async Task Execute(Func<Task> action, string? message = null)
    {
        if (_busy || _closing) return;
        _busy = true;
        MessageLabel.Visibility = Visibility.Collapsed;
        RefreshState();
        try
        {
            await action();
            if (message != null && !_closing) SetMessage(message, false);
        }
        catch (Exception ex)
        {
            if (!_closing) SetMessage(ex.Message, true);
        }
        finally
        {
            _busy = false;
            if (!_closing) RefreshState();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
        if (e.Cancel) _closing = false;
    }

    void SetMessage(string text, bool error)
    {
        MessageLabel.Text = text;
        MessageLabel.Visibility = Visibility.Visible;
        if (error) _notify?.Invoke(text, true);
    }

    void ClosingAction(Action action)
    {
        try { Hide(); action(); }
        catch (Exception ex) { if (!_closing) SetMessage(ex.Message, true); }
    }
    void OnOpen(object sender, RoutedEventArgs e) => ClosingAction(_host.Show);
    void OnExit(object sender, RoutedEventArgs e) => ClosingAction(_host.Exit);
    void OnElevate(object sender, RoutedEventArgs e) => ClosingAction(_host.Elevate);
    async void OnToggleCore(object sender, RoutedEventArgs e) => await Execute(async () =>
    {
        if (_host.Running()) _host.Stop();
        else
        {
            var status = await _host.Start();
            if (status.NeedsAdmin || status.Message == "admin-required") { Hide(); _host.Elevate(); }
            else if (!status.Running) throw new InvalidOperationException("Не удалось запустить подключение.");
        }
    });
    async void OnRestart(object sender, RoutedEventArgs e) => await Execute(() => _host.Restart());
    async void OnPanic(object sender, RoutedEventArgs e) => await Execute(async () =>
    {
        if (_host.Panic == null) throw new InvalidOperationException("Экстренное отключение недоступно.");
        var message = await _host.Panic();
        SetMessage(message, false);
    });
    async void OnCopy(object sender, RoutedEventArgs e) => await Execute(() =>
    {
        _host.CopyPort();
        return Task.CompletedTask;
    }, "Адрес скопирован");
    async void OnTun(object sender, RoutedEventArgs e) => await Execute(async () =>
    {
        var enabled = !_host.Settings().TunEnabled;
        _host.Patch(s => { s.TunEnabled = enabled; if (enabled) s.SystemProxy = false; });
        if (_host.Running()) await _host.Restart();
    });
    async void OnProxy(object sender, RoutedEventArgs e) => await Execute(() =>
    {
        var settings = _host.Settings();
        _host.SystemProxy(!(settings.SystemProxy && !settings.TunEnabled));
        return Task.CompletedTask;
    });
    async void OnMode(object sender, RoutedEventArgs e) => await Execute(async () =>
    {
        var mode = ((FrameworkElement)sender).Tag as string ?? "rule";
        if (_host.Settings().Mode == mode) return;
        _host.Patch(s => s.Mode = mode);
        if (_host.Running()) await _host.Restart();
    });

    void SetBrush(string key, string color)
    {
        try { Resources[key] = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(color)); }
        catch (FormatException) { /* Validated settings normally supply six-digit colors. */ }
    }
    static bool IsLight(string? hex)
    {
        try
        {
            var c = (WpfColor)ColorConverter.ConvertFromString(hex ?? "#0B101B");
            return (.2126 * c.R + .7152 * c.G + .0722 * c.B) > 165;
        }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr window);
}
