using System.Threading;
using System.Windows;
using Jester.Host.Services;
using WinApp = System.Windows.Application;

namespace Jester.Host;

public partial class App : WinApp
{
    Mutex? _mutex;
    SettingsStore? _settings;
    ProfileStore? _profiles;
    CoreManager? _core;
    JesterApi? _api;
    TrayService? _tray;
    NetworkGuardian? _guardian;
    System.Windows.Threading.DispatcherTimer? _subscriptionTimer;
    readonly CancellationTokenSource _shutdown = new();
    int _updatingSubscriptions;

    void OnStartup(object sender, StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\JesterProxyClient.SingleInstance", out var created);
        if (!created)
        {
            System.Windows.MessageBox.Show("Jester уже запущен.", "Jester", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.Ensure();
        _settings = new SettingsStore();
        _settings.Load();
        _profiles = new ProfileStore();
        _core = new CoreManager(_settings, _profiles, new CoreDownloader());
        _api = new JesterApi(_settings, _profiles, _core);
        _guardian = new NetworkGuardian(_settings, _core);
        _core.Guardian = _guardian;
        _guardian.Start();
        if (_settings.Current.DeadManSwitch && _settings.Current.DeadManArmed)
            _guardian.ArmFailClosed();
        try { _api.Start(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Не удалось открыть локальный порт 37841.\n" + ex.Message, "Jester");
            Shutdown();
            return;
        }

        var window = new MainWindow(_settings);
        WindowActions.ApplyAppearance = () => window.Dispatcher.Invoke(window.ApplyAppearance);
        _tray = new TrayService(new TrayHost
        {
            Show = () =>
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            },
            Exit = () =>
            {
                _core.Stop();
                Shutdown();
            },
            Running = () => _core.Running,
            Start = () => _core.StartAsync(),
            Stop = () => _core.Stop(),
            Restart = () => _core.RestartAsync(),
            Elevate = () => _core.Elevate(),
            Settings = () => _settings.Current,
            Patch = mutate => _settings.Patch(mutate),
            CopyPort = () =>
            {
                var port = _settings.Current.MixedPort;
                Current.Dispatcher.Invoke(() => System.Windows.Clipboard.SetText("127.0.0.1:" + port));
            },
            SystemProxy = on =>
            {
                if (on)
                {
                    var needRestart = _core.Running && _settings.Current.TunEnabled;
                    _settings.Patch(s =>
                    {
                        s.SystemProxy = true;
                        s.TunEnabled = false;
                    });
                    if (needRestart) _ = _core.RestartAsync();
                    else if (_core.Running) Services.SystemProxy.Set(true, _settings.Current.MixedPort);
                }
                else
                {
                    Services.SystemProxy.Set(false, 0);
                    _settings.Patch(s => s.SystemProxy = false);
                }
            },
            SelectedNode = () => _settings.Current.LastSelectedNode,
            Panic = async () =>
            {
                var r = await _core.PhantomPanicAsync();
                return r.Message;
            }
        });

        StartSubscriptionTimer();

        WindowActions.Minimize = () => window.Dispatcher.Invoke(() => window.WindowState = WindowState.Minimized);
        WindowActions.ToggleMaximize = () => window.Dispatcher.Invoke(() =>
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized);
        WindowActions.CloseOrTray = hide => window.Dispatcher.Invoke(() =>
        {
            if (hide)
            {
                window.Hide();
                return;
            }
            _core.Stop();
            Shutdown();
        });
        WindowActions.ApplyCapture = () =>
        {
            try { Current.Dispatcher.Invoke(() => window.ApplyCaptureProtection()); }
            catch { /* ignore */ }
        };
        WindowActions.Drag = () =>
        {
            try
            {
                // Handled by MainWindow WebView message; keep callback for API parity.
                window.DragMove();
            }
            catch { /* ignore */ }
        };
        WindowActions.PickImage = () =>
        {
            try
            {
                return Current.Dispatcher.Invoke(() =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "Фон окна",
                        Filter = "Изображения|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp|Все файлы|*.*",
                        CheckFileExists = true
                    };
                    return dlg.ShowDialog(window) == true ? dlg.FileName : null;
                });
            }
            catch { return null; }
        };

        if (_settings.Current.StartHidden) window.Hide();
        else window.Show();
    }

    void StartSubscriptionTimer()
    {
        _subscriptionTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _subscriptionTimer.Tick += async (_, _) => await TickSubscriptions();
        _subscriptionTimer.Start();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(8000, _shutdown.Token); await TickSubscriptions(); }
            catch { /* ignore */ }
        });
    }

    async Task TickSubscriptions()
    {
        if (Interlocked.CompareExchange(ref _updatingSubscriptions, 1, 0) != 0) return;
        try
        {
            if (_settings == null || _profiles == null || _core == null) return;
            var s = _settings.Current;
            if (!s.AutoUpdateSubscriptions) return;
            var hours = Math.Max(1, s.AutoUpdateIntervalHours);
            if (s.LastSubscriptionUpdateAt is { } last
                && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(hours))
                return;
            var pid = _core.Pid;
            var (updated, _, _) = await _profiles.UpdateAllSubscriptionsAsync(_shutdown.Token);
            _shutdown.Token.ThrowIfCancellationRequested();
            _settings.Patch(x => x.LastSubscriptionUpdateAt = DateTimeOffset.UtcNow);
            if (updated > 0 && _core.Running && pid == _core.Pid)
                await _core.RestartIfRunningAsync(_shutdown.Token);
        }
        catch { /* ignore background failures */ }
        finally { Interlocked.Exchange(ref _updatingSubscriptions, 0); }
    }

    void OnExit(object sender, ExitEventArgs e)
    {
        // Keep DeadManArmed across restarts so startup can re-arm the sink.
        _shutdown.Cancel();
        _subscriptionTimer?.Stop();
        _guardian?.Dispose();
        _core?.Dispose();
        _api?.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
    }
}
