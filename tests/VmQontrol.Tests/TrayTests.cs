using Avalonia.Controls;
using VmQontrol.App.Platform;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

/// <summary>
/// Phase 19 item 9 (s11): the tray icon, opt-in. Its menu lists the machines with what each can do; with it on, the
/// window's close button (the window manager's close request) hides to the tray, and Quit ends the app.
/// </summary>
public sealed class TrayTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task With_the_tray_on_the_close_button_hides_and_Quit_ends_and_the_menu_lists_the_machines()
    {
        using var profile = new TestProfile("tray");
        foreach (var name in (string[])["Web", "Build"])
        {
            var dir = Path.Combine(profile.Paths.DefaultLibrary, name);
            Directory.CreateDirectory(dir);
            MachineStore.Save(new MachineConfig { Name = name, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
            File.WriteAllBytes(Path.Combine(dir, "disk0.qcow2"), []);
        }

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1200, Height = 800 };
            var quit = 0;
            window.Tray = new TrayController(Avalonia.Application.Current!, () => quit++, shell, window);
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 2, "the library");

            // The window manager's close request (the title bar's ×), as the platform delivers it.
            // Avalonia keeps the platform window's close hook internal, so it is reached by name; it returns "cancelled".
            bool CloseButton()
            {
                var impl = window.PlatformImpl!;
                var hook = impl.GetType().GetInterfaces().Concat([impl.GetType()]).Select(t => t.GetProperty("Closing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    .FirstOrDefault(p => p is not null)?.GetValue(impl) as Func<WindowCloseReason, bool> ?? throw new InvalidOperationException("no close hook on " + impl.GetType());
                return hook(WindowCloseReason.WindowClosing);
            }

            var offIcon = window.Tray.Icon is null;

            // On, from Preferences' switch: the icon comes, the close button hides instead.
            shell.UpdateSettings(s => s with { TrayIcon = true });
            Ui.Pump(0.2);
            var onIcon = window.Tray.Icon is not null;
            var menu = window.Tray.Icon!.Menu!.Items.Select(i => (i as NativeMenuItem)?.Header ?? "—").ToList();
            var webItems = ((NativeMenuItem)window.Tray.Icon.Menu.Items.First(i => (i as NativeMenuItem)?.Header?.StartsWith("Web", StringComparison.Ordinal) == true)).Menu!.Items
                .Select(i => ((NativeMenuItem)i).Header).ToList();
            var tip = window.Tray.Icon.ToolTipText;
            var onCancelled = CloseButton();
            Ui.Pump(0.2);
            var hidden = !window.IsVisible;
            window.Tray.ShowCommand.Execute(null);
            Ui.Pump(0.2);
            var shownAgain = window.IsVisible;

            // Quit (the tray's menu or Ctrl+Q) really ends the app: the close is not turned into a hide.
            window.Tray.QuitCommand.Execute(null);
            var quitting = window.Tray.Quitting && quit == 1;
            shell.UpdateSettings(s => s with { TrayIcon = false });
            Ui.Pump(0.2);
            var iconGone = window.Tray.Icon is null;
            // Control: tray off → the same close button closes the window for real.
            var offCancelled = CloseButton();
            return (offIcon, offCancelled, onIcon, menu, webItems, tip, onCancelled, hidden, shownAgain, quitting, iconGone);
        });

        Assert.True(seen.offIcon);
        Assert.False(seen.offCancelled);
        Assert.True(seen.onIcon);
        Assert.Equal(["Show VM Qontrol", "-", "Build — Powered off", "Web — Powered off", "-", "Quit VM Qontrol"], seen.menu); // "-" = a separator
        Assert.Equal(["Power on"], seen.webItems);
        Assert.Equal("VM Qontrol: no machine running", seen.tip);
        Assert.True(seen.onCancelled);
        Assert.True(seen.hidden);
        Assert.True(seen.shownAgain);
        Assert.True(seen.quitting);
        Assert.True(seen.iconGone);
    }
}
