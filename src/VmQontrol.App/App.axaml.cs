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

    public override void OnFrameworkInitializationCompleted()
    {
        // The watch posts to the dispatcher from its own thread, so it may only start once the dispatcher exists here.
        if (WatchUiThread) UiStallWatch.Start(Environment.GetEnvironmentVariable("VMQONTROL_UI_TRACE") == "1");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = Paths ?? QontrolPaths.ForCurrentUser();
            var shell = new ViewModels.ShellViewModel(paths, Settings ?? AppSettings.Load(paths)) { OpenOnStart = OpenOnStart };
            var settings = shell.Settings;
            // Started at sign-in (the autostart entry) with "start minimized": the window waits on the taskbar.
            var minimized = settings.StartMinimized && (desktop.Args ?? []).Contains(VmQontrol.Core.Hosts.Autostart.Argument);
            desktop.MainWindow = new MainWindow { DataContext = shell, WindowState = minimized ? Avalonia.Controls.WindowState.Minimized : Avalonia.Controls.WindowState.Normal };
            _ = shell.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
