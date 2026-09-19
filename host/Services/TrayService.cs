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
}

public sealed class TrayService : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly Icon? _baseIcon;
    readonly TrayHost _host;
    readonly ToolStripMenuItem _run;
    readonly ToolStripMenuItem _tun;
    readonly ToolStripMenuItem _proxy;
    readonly ToolStripMenuItem _rule;
    readonly ToolStripMenuItem _global;
    readonly ToolStripMenuItem _direct;
    readonly System.Windows.Forms.Timer _pulse;
    Icon? _current;
    bool? _lastRunning;

    public TrayService(TrayHost host)
    {
        _host = host;
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "jester.ico");
        _baseIcon = File.Exists(icoPath) ? new Icon(icoPath, 16, 16) : null;
        _icon = new WinForms.NotifyIcon
        {
            Text = "Jester",
            Visible = true,
            Icon = _baseIcon ?? SystemIcons.Application
        };
        ApplyStatus(false);

        var menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(10, 10, 10),
            Renderer = new ToolStripProfessionalRenderer(new InkColors())
        };

        menu.Items.Add(Item("Открыть", (_, _) => _host.Show()));
        menu.Items.Add(new ToolStripSeparator());

        _run = Item("Запустить", async (_, _) => await ToggleCore());
        menu.Items.Add(_run);
        menu.Items.Add(Item("Перезапуск", async (_, _) =>
        {
            try { await _host.Restart(); Tip("Перезапущен"); }
            catch (Exception ex) { Tip(ex.Message, error: true); }
        }));
        menu.Items.Add(new ToolStripSeparator());

        _tun = Check("TUN", () =>
        {
            var on = !_host.Settings().TunEnabled;
            _host.Patch(s => s.TunEnabled = on);
            if (_host.Running()) _ = SafeRestart();
        });
        _proxy = Check("Системный прокси", () =>
        {
            var on = !_host.Settings().SystemProxy;
            _host.SystemProxy(on);
            _host.Patch(s => s.SystemProxy = on);
        });
        menu.Items.Add(_tun);
        menu.Items.Add(_proxy);
        menu.Items.Add(new ToolStripSeparator());

        _rule = Check("Режим: по правилам", () => SetMode("rule"));
        _global = Check("Режим: глобальный", () => SetMode("global"));
        _direct = Check("Режим: прямое соединение", () => SetMode("direct"));
        menu.Items.Add(_rule);
        menu.Items.Add(_global);
        menu.Items.Add(_direct);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Скопировать адрес", (_, _) => _host.CopyPort()));
        menu.Items.Add(Item("Перезапуск с правами администратора", (_, _) => _host.Elevate()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Выход", (_, _) => _host.Exit()));

        menu.Opening += (_, _) => Sync();
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => _host.Show();
        _icon.MouseMove += (_, _) =>
        {
            _icon.Text = _host.Running() ? "Jester · в маске" : "Jester · тишина";
        };

        _pulse = new System.Windows.Forms.Timer { Interval = 1200 };
        _pulse.Tick += (_, _) => ApplyStatus(_host.Running());
        _pulse.Start();
    }

    void ApplyStatus(bool running)
    {
        if (_lastRunning == running && _current != null) return;
        _lastRunning = running;
        var next = BuildStatusIcon(running);
        var prev = _current;
        _current = next;
        _icon.Icon = next;
        if (prev != null && !ReferenceEquals(prev, _baseIcon)) prev.Dispose();
        _icon.Text = running ? "Jester · в маске" : "Jester · тишина";
    }

    Icon BuildStatusIcon(bool running)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (_baseIcon != null)
                g.DrawIcon(_baseIcon, new Rectangle(0, 0, 16, 16));
            else
            {
                using var fill = new SolidBrush(Color.FromArgb(245, 245, 245));
                g.FillEllipse(fill, 2, 2, 12, 12);
            }

            var dot = running ? Color.FromArgb(52, 211, 98) : Color.FromArgb(140, 140, 140);
            using var ring = new SolidBrush(Color.FromArgb(18, 18, 18));
            using var brush = new SolidBrush(dot);
            g.FillEllipse(ring, 9, 9, 7, 7);
            g.FillEllipse(brush, 10, 10, 5, 5);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool DestroyIcon(IntPtr handle);

    void Sync()
    {
        var s = _host.Settings();
        var on = _host.Running();
        _run.Text = on ? "Остановить" : "Запустить";
        _tun.Checked = s.TunEnabled;
        _proxy.Checked = s.SystemProxy;
        _rule.Checked = s.Mode == "rule";
        _global.Checked = s.Mode == "global";
        _direct.Checked = s.Mode == "direct";
        ApplyStatus(on);
    }

    async Task ToggleCore()
    {
        try
        {
            if (_host.Running())
            {
                _host.Stop();
                ApplyStatus(false);
                Tip("Остановлен");
                return;
            }
            var status = await _host.Start();
            if (status.NeedsAdmin || status.Message == "admin-required")
            {
                _host.Elevate();
                return;
            }
            ApplyStatus(true);
            Tip("Запущен");
        }
        catch (Exception ex) { Tip(ex.Message, error: true); }
    }

    void SetMode(string mode)
    {
        _host.Patch(s => s.Mode = mode);
        if (_host.Running()) _ = SafeRestart();
    }

    async Task SafeRestart()
    {
        try { await _host.Restart(); }
        catch (Exception ex) { Tip(ex.Message, error: true); }
        finally { ApplyStatus(_host.Running()); }
    }

    void Tip(string text, bool error = false)
    {
        _icon.ShowBalloonTip(1800, "Jester", text, error ? ToolTipIcon.Error : ToolTipIcon.None);
    }

    static ToolStripMenuItem Item(string text, EventHandler onClick)
    {
        var i = new ToolStripMenuItem(text) { ForeColor = Color.White };
        i.Click += onClick;
        return i;
    }

    static ToolStripMenuItem Check(string text, Action onClick)
    {
        var i = new ToolStripMenuItem(text) { ForeColor = Color.White };
        i.Click += (_, _) => onClick();
        return i;
    }

    public void Dispose()
    {
        _pulse.Stop();
        _pulse.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        if (_current != null && !ReferenceEquals(_current, _baseIcon)) _current.Dispose();
        _baseIcon?.Dispose();
    }

    sealed class InkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(10, 10, 10);
        static readonly Color Hi = Color.FromArgb(32, 32, 32);
        public override Color MenuBorder => Color.FromArgb(48, 48, 48);
        public override Color MenuItemBorder => Color.FromArgb(48, 48, 48);
        public override Color MenuItemSelected => Hi;
        public override Color MenuItemSelectedGradientBegin => Hi;
        public override Color MenuItemSelectedGradientEnd => Hi;
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Color.FromArgb(36, 36, 36);
        public override Color SeparatorLight => Color.FromArgb(36, 36, 36);
    }
}
