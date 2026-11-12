using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Jester.Host.Models;
using WinForms = System.Windows.Forms;

namespace Jester.Host.Services;

public sealed class TrayHost
{
    public required Action Show { get; init; }
    public required Action Exit { get; init; }
    public required Func<bool> Running { get; init; }
    public required Func<Task<CoreStatus>> Start { get; init; }
    public required Action Stop { get; init; }
    public required Func<Task> Restart { get; init; }
    public required Action Elevate { get; init; }
    public required Func<AppSettings> Settings { get; init; }
    public required Action<Action<AppSettings>> Patch { get; init; }
    public required Action CopyPort { get; init; }
    public required Action<bool> SystemProxy { get; init; }
    public Func<string>? SelectedNode { get; init; }
    public Func<Task<string>>? Panic { get; init; }
}

public sealed class TrayService : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly Icon? _baseIcon;
    readonly TrayHost _host;
    readonly System.Windows.Threading.Dispatcher _dispatcher;
    readonly WinForms.ContextMenuStrip _menuBridge;
    readonly WinForms.Timer _pulse;
    Jester.Host.TrayFlyout? _flyout;
    Icon? _current;
    bool? _lastRunning;
    bool _disposed;
    DateTime _lastTooltipUpdate;

    public TrayService(TrayHost host)
    {
        _host = host;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "jester.ico");
        _baseIcon = File.Exists(icoPath) ? new Icon(icoPath, 16, 16) : null;
        _icon = new WinForms.NotifyIcon
        {
            Text = "Jester",
            Visible = true,
            Icon = _baseIcon ?? SystemIcons.Application
        };
        // NotifyIcon routes keyboard context-menu requests through ContextMenuStrip.
        // Cancel before painting; only our native WPF flyout is ever displayed.
        _menuBridge = new WinForms.ContextMenuStrip();
        _menuBridge.Items.Add("Jester");
        _menuBridge.Opening += (_, e) =>
        {
            e.Cancel = true;
            if (!_disposed && !_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(ShowFlyout));
        };
        _icon.ContextMenuStrip = _menuBridge;
        _icon.DoubleClick += (_, _) =>
        {
            _flyout?.Hide();
            _host.Show();
        };
        _icon.MouseMove += (_, _) => UpdateTooltip();
        ApplyStatus(_host.Running());

        _pulse = new WinForms.Timer { Interval = 1200 };
        _pulse.Tick += (_, _) =>
        {
            if (_disposed) return;
            ApplyStatus(_host.Running());
            if (_flyout is { IsVisible: true }) _flyout.RefreshState();
        };
        _pulse.Start();
    }

    public void ShowFlyout()
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(new Action(ShowFlyout)); return; }
        _flyout ??= new Jester.Host.TrayFlyout(_host, Tip);
        _flyout.ShowAtCursor();
    }

    void UpdateTooltip()
    {
        if (_disposed || DateTime.UtcNow - _lastTooltipUpdate < TimeSpan.FromMilliseconds(250)) return;
        _lastTooltipUpdate = DateTime.UtcNow;
        var on = _host.Running();
        var node = _host.SelectedNode?.Invoke() ?? _host.Settings().LastSelectedNode;
        var tip = on ? "Jester · активен" : "Jester · остановлен";
        if (!string.IsNullOrWhiteSpace(node)) tip += " · " + Trim(node, 40);
        _icon.Text = tip.Length > 63 ? tip[..60] + "…" : tip;
    }

    void ApplyStatus(bool running)
    {
        if (_lastRunning == running && _current != null) return;
        _lastRunning = running;
        var next = BuildStatusIcon(running);
        var previous = _current;
        _current = next;
        _icon.Icon = next;
        if (previous != null && !ReferenceEquals(previous, _baseIcon)) previous.Dispose();
        _lastTooltipUpdate = DateTime.MinValue;
        UpdateTooltip();
    }

    Icon BuildStatusIcon(bool running)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (_baseIcon != null) graphics.DrawIcon(_baseIcon, new Rectangle(0, 0, 16, 16));
            else
            {
                using var fill = new SolidBrush(Color.FromArgb(228, 238, 255));
                graphics.FillEllipse(fill, 2, 2, 12, 12);
            }
            using var ring = new SolidBrush(Color.FromArgb(18, 26, 43));
            using var dot = new SolidBrush(running ? Color.FromArgb(149, 224, 200) : Color.FromArgb(145, 158, 180));
            graphics.FillEllipse(ring, 9, 9, 7, 7);
            graphics.FillEllipse(dot, 10, 10, 5, 5);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool DestroyIcon(IntPtr handle);

    void Tip(string text, bool error)
    {
        if (!_disposed) _icon.ShowBalloonTip(1800, "Jester", text, error ? ToolTipIcon.Error : ToolTipIcon.None);
    }

    static string Trim(string value, int maximum) => value.Length <= maximum ? value : value[..(maximum - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pulse.Stop();
        _pulse.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menuBridge.Dispose();
        if (_flyout != null)
        {
            if (_dispatcher.CheckAccess()) _flyout.Close();
            else if (!_dispatcher.HasShutdownStarted) _dispatcher.Invoke(() => _flyout.Close());
            _flyout = null;
        }
        if (_current != null && !ReferenceEquals(_current, _baseIcon)) _current.Dispose();
        _baseIcon?.Dispose();
    }
}
