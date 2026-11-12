using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jester.Host;
using Jester.Host.Models;
using Jester.Host.Services;
using WpfApp = System.Windows.Application;

namespace Jester.Regression;

/// <summary>
/// Opt-in native material inspection. It never runs the production App startup,
/// loads user settings/profiles, or constructs CoreManager and NetworkGuardian.
/// Serve the synthetic UI fixture on 127.0.0.1:5173 before using --preview-material.
/// </summary>
internal static class MaterialPreview
{
    public static int Run(string[] args)
    {
        var trayOnly = args.Contains("--preview-tray", StringComparer.OrdinalIgnoreCase);
        WindowMaterial.PreviewAccentMode = args.Contains("--accent4", StringComparer.OrdinalIgnoreCase) ? 4 : 0;
        if (!trayOnly && WebViewRuntime.FindInstalledFolder() == null && !WebViewRuntime.RegistryInstalled())
        {
            Console.Error.WriteLine("Native preview needs an already installed WebView2 runtime; no runtime will be downloaded by this harness.");
            return 1;
        }

        var previousDevelopment = Environment.GetEnvironmentVariable("JESTER_DEV");
        Environment.SetEnvironmentVariable("JESTER_DEV", "1");
        var application = new WpfApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var backdrop = CreateBackdrop();
        Window? preview = null;
        try
        {
            if (trayOnly)
            {
                var settings = new AppSettings { TunEnabled = true, FirstRunCompleted = true, LastSelectedNode = "Synthetic preview · Netherlands" };
                var running = true;
                var host = new TrayHost
                {
                    Show = () => Console.WriteLine("PREVIEW: Show"),
                    Exit = () => application.Shutdown(),
                    Running = () => running,
                    Start = () => { running = true; return Task.FromResult(new CoreStatus { Running = true, Message = "Synthetic preview" }); },
                    Stop = () => running = false,
                    Restart = () => Task.CompletedTask,
                    Elevate = () => Console.WriteLine("PREVIEW: Elevate (no process launched)"),
                    Settings = () => settings,
                    Patch = mutate => mutate(settings),
                    CopyPort = () => Console.WriteLine("PREVIEW: CopyPort (clipboard unchanged)"),
                    SystemProxy = enabled => { settings.SystemProxy = enabled; if (enabled) settings.TunEnabled = false; },
                    SelectedNode = () => settings.LastSelectedNode,
                    Panic = () => { running = false; return Task.FromResult("Synthetic preview only; no network state changed."); }
                };
                preview = new TrayFlyout(host, (message, error) => Console.WriteLine($"PREVIEW {(error ? "ERROR" : "INFO")}: {message}")) { KeepOpenForPreview = true };
                preview.Title = "Jester Tray Material Preview";
                preview.WindowStartupLocation = WindowStartupLocation.Manual;
                preview.Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - preview.Width) / 2;
                preview.Top = SystemParameters.WorkArea.Top + 60;
                var shown = false;
                preview.ContentRendered += (_, _) => shown = true;
                preview.IsVisibleChanged += (_, _) => { if (shown && !preview.IsVisible) application.Shutdown(); };
            }
            else
            {
                var previewData = Path.Combine(Workspace(), ".test-artifacts", "webview-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(previewData);
                // This constructor stores browser cache only under the workspace.
                var main = new MainWindow(new SettingsStore(), previewData)
                {
                    Title = "Jester Material Preview",
                    Width = Math.Min(1280, SystemParameters.WorkArea.Width - 80),
                    Height = Math.Min(820, SystemParameters.WorkArea.Height - 80),
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                preview = main;
                WindowActions.Minimize = () => main.WindowState = WindowState.Minimized;
                WindowActions.ToggleMaximize = () => main.WindowState = main.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                WindowActions.CloseOrTray = _ => main.Close();
            }

            preview.Closed += (_, _) => application.Shutdown();
            preview.ContentRendered += (_, _) => Console.WriteLine("Native material active: " + (preview switch
            {
                MainWindow mainWindow => mainWindow.NativeGlassActive,
                TrayFlyout trayWindow => trayWindow.NativeMaterialActive,
                _ => false
            }));
            application.MainWindow = preview;
            backdrop.Show();
            preview.Owner = backdrop;
            Console.WriteLine("Native material preview uses synthetic data and a static test-pattern window behind the UI.");
            Console.WriteLine("No production App startup, profile loading, proxy settings, or tunnel will be used.");
            return application.Run(preview);
        }
        finally
        {
            WindowActions.Minimize = null;
            WindowActions.ToggleMaximize = null;
            WindowActions.CloseOrTray = null;
            Environment.SetEnvironmentVariable("JESTER_DEV", previousDevelopment);
        }
    }

    static Window CreateBackdrop()
    {
        var colors = new[] { "#1E6DAA", "#4C367E", "#2B8B82", "#B17A35" };
        var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(24, 40, 62)) };
        for (var index = 0; index < colors.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var tint = (Color)ColorConverter.ConvertFromString(colors[index]);
            var panel = new Grid
            {
                Background = new LinearGradientBrush(tint, Color.FromRgb(18, 32, 51), 65)
            };
            panel.Children.Add(new TextBlock
            {
                Text = index % 2 == 0 ? "ПРОЗРАЧНОСТЬ" : "GLASS TEST",
                Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                FontSize = 44,
                FontWeight = FontWeights.Light,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24)
            });
            Grid.SetColumn(panel, index);
            grid.Children.Add(panel);
        }

        return new Window
        {
            Title = "Jester Material Test Backdrop",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Left = SystemParameters.WorkArea.Left,
            Top = SystemParameters.WorkArea.Top,
            Width = SystemParameters.WorkArea.Width,
            Height = SystemParameters.WorkArea.Height,
            Content = grid
        };
    }

    static string Workspace()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "host", "Jester.Host.csproj"))) return directory.FullName;
        throw new InvalidOperationException("Run native material preview from the Jester workspace.");
    }
}
