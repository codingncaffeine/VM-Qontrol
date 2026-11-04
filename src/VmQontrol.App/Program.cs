using Avalonia;
using VmQontrol.App.Platform;
using VmQontrol.Core;

namespace VmQontrol.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == Core.Runtime.SegmentServer.Argument)
        {
            // A private segment's address server, started by the first machine on it; no window.
            var segmentPaths = QontrolPaths.ForCurrentUser();
            segmentPaths.EnsureCreated();
            Log.Initialize("vmq-segment", segmentPaths.Logs);
            return Core.Runtime.SegmentServer.Main(segmentPaths, args);
        }

        if (args.Length > 0 && args[0] == Core.Runtime.ShutdownGuard.Argument)
        {
            // Saves the running machines if this computer shuts down; started with the first machine, no window.
            var guardPaths = QontrolPaths.ForCurrentUser();
            guardPaths.EnsureCreated();
            Log.Initialize("vmq-guard", guardPaths.Logs);
            return Core.Runtime.ShutdownGuard.Main(guardPaths);
        }

        if (args.Length > 0 && args[0] == Core.Runtime.SharedFolders.Argument)
        {
            // Sets a machine's shared folders up inside its guest once the guest agent answers; started at power on, no window.
            var sharePaths = QontrolPaths.ForCurrentUser();
            sharePaths.EnsureCreated();
            Log.Initialize("vmq-shares", sharePaths.Logs);
            return Core.Runtime.SharedFolders.Main(sharePaths, args);
        }

        if (args.Length > 2 && args[0] == Core.WindowsApps.WindowsAppsMenu.Argument)
        {
            // An app menu entry of a Windows program (D43): a small window opens it; the main window stays closed.
            App.WindowsApp = (args[1], args[2]);
        }

        if (args.Any(a => a is "--version" or "-V"))
        {
            Console.WriteLine($"VM Qontrol {BuildInfo.Stamp}");
            Console.WriteLine(BuildInfo.License);
            return 0;
        }

        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine($"""
                Usage: vmqontrol [options] [MACHINE]

                  MACHINE             Open on this machine's page (its name in the library).

                  --version           Print the version and license, then exit.

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
        App.OpenOnStart = App.WindowsApp is null ? args.FirstOrDefault(a => !a.StartsWith('-')) : null;
        App.WatchUiThread = true;
        // A build run from its folder has no desktop entry, so the taskbar would show a generic icon (an isolated
        // profile is a trial or a test: it never touches the desktop).
        if (!paths.Isolated && App.WindowsApp is null) _ = Task.Run(() => DesktopIntegration.Ensure());

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
