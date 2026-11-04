using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using VmQontrol.App.Views;
using VmQontrol.Core;

namespace VmQontrol.App;

public class App : Application
{
    /// <summary>The profile's folders, set by <see cref="Program"/> (or a test) before the app starts.</summary>
    public static QontrolPaths? Paths { get; set; }

    /// <summary>A machine to open when the library has loaded (the command line's MACHINE).</summary>
    public static string? OpenOnStart { get; set; }

    /// <summary>A Windows program to open from an app menu entry (machine id, app slug); no main window then.</summary>
    public static (string Machine, string App)? WindowsApp { get; set; }

    /// <summary>The settings, read by <see cref="Program"/> before the UI thread starts.</summary>
    public static AppSettings? Settings { get; set; }

    /// <summary>Starts the UI stall watch once Avalonia owns the UI thread (set by <see cref="Program"/>, not by tests).</summary>
    public static bool WatchUiThread { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Controls.RowLabels.Install();
        // VMQONTROL_THEME=dark|light pins the theme (screenshots, bug reports); otherwise the desktop's choice is followed.
        RequestedThemeVariant = Environment.GetEnvironmentVariable("VMQONTROL_THEME") switch
        {
            "dark" => ThemeVariant.Dark,
            "light" => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
    }

    private System.Runtime.InteropServices.PosixSignalRegistration? _sigterm;

    public override void OnFrameworkInitializationCompleted()
    {
        // The watch posts to the dispatcher from its own thread, so it may only start once the dispatcher exists here.
        if (WatchUiThread) UiStallWatch.Start(Environment.GetEnvironmentVariable("VMQONTROL_UI_TRACE") == "1");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime launch && WindowsApp is { } program)
        {
            var vm = new ViewModels.WindowsAppLaunchViewModel(Paths ?? QontrolPaths.ForCurrentUser(), program.Machine, program.App);
            launch.MainWindow = new WindowsAppLaunchWindow { DataContext = vm };
            _ = vm.RunAsync();
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = Paths ?? QontrolPaths.ForCurrentUser();
            var shell = new ViewModels.ShellViewModel(paths, Settings ?? AppSettings.Load(paths)) { OpenOnStart = OpenOnStart };
            var settings = shell.Settings;
            // Started at sign-in (the autostart entry) with "start minimized": the window waits on the taskbar.
            var minimized = settings.StartMinimized && (desktop.Args ?? []).Contains(VmQontrol.Core.Hosts.Autostart.Argument);
            var window = new MainWindow { DataContext = shell, WindowState = minimized ? Avalonia.Controls.WindowState.Minimized : Avalonia.Controls.WindowState.Normal };
            window.RestorePlacement(settings.MainWindow);
            desktop.MainWindow = window;
            window.Tray = new Platform.TrayController(this, () => desktop.Shutdown(), shell, window);
            // The window's size is saved as it closes; the save runs on a worker, so the exit waits a moment for it.
            desktop.Exit += (_, _) => AppSettings.WaitForSaves(TimeSpan.FromSeconds(2));
            // SIGTERM (a desktop logging out, systemd, kill) closes the window as its close button does, so what is saved
            // at close (the window's size) is saved; machines run on in their own processes either way.
            _sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => window.Quit());
            });
            _ = shell.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
