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
        try { _api.Start(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Не удалось открыть локальный порт 37841.\n" + ex.Message, "Jester");
            Shutdown();
            return;
        }

        var window = new MainWindow(_settings);
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
                    else Services.SystemProxy.Set(true, _settings.Current.MixedPort);
                }
                else
                {
                    Services.SystemProxy.Set(false, 0);
                    _settings.Patch(s => s.SystemProxy = false);
                }
            }
        });

        WindowActions.Minimize = () => window.WindowState = WindowState.Minimized;
        WindowActions.ToggleMaximize = () =>
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        WindowActions.CloseOrTray = hide =>
        {
            if (hide)
            {
                window.Hide();
                return;
            }
            _core.Stop();
            Shutdown();
        };
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

    void OnExit(object sender, ExitEventArgs e)
    {
        _core?.Stop();
        _api?.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
    }
}
