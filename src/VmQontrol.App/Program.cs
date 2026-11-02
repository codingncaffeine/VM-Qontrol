using Avalonia;
using VmQontrol.App.Platform;
using VmQontrol.Core;

namespace VmQontrol.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a is "--version" or "-V"))
        {
            Console.WriteLine($"VM Qontrol {BuildInfo.Stamp}");
            Console.WriteLine(BuildInfo.Licence);
            return 0;
        }

        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine($"""
                Usage: vmqontrol [options] [MACHINE]

                  MACHINE             Open on this machine's page (its name in the library).

                  --version           Print the version and licence, then exit.

                {QontrolPaths.HomeVariable}=DIR keeps every setting, log and machine under DIR instead of the usual folders.
                VMQONTROL_UI_TRACE=1 logs every moment the window's thread was busy for more than 4 ms.
                VMQONTROL_WAYLAND=0 runs on X11 (through XWayland); =1 forces native Wayland.
                The command-line tool is vmq.
                """);
            return 0;
        }

        // Read before the UI thread starts: nothing that touches the disk runs on it.
        var paths = QontrolPaths.ForCurrentUser();
        paths.EnsureCreated();
        Log.Initialize("vmqontrol", paths.Logs);
        if (paths.Isolated) Log.Info($"Isolated profile under {QontrolPaths.HomeVariable}");
        App.Paths = paths;
        App.Settings = AppSettings.Load(paths);
        App.OpenOnStart = args.FirstOrDefault(a => !a.StartsWith('-'));
        App.WatchUiThread = true;
        // A build run from its folder has no desktop entry, so the taskbar would show a generic icon (an isolated
        // profile is a trial or a test: it never touches the desktop).
        if (!paths.Isolated) _ = Task.Run(() => DesktopIntegration.Ensure());

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Log.Crash("startup", ex));
            return 1;
        }
        finally
        {
            UiStallWatch.Current?.Stop();
            Log.Flush(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Used by <see cref="Main"/>, the designer and the headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        WindowingBackend.Apply(
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace());
}
