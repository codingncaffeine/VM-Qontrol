using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Devices;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

/// <summary>
/// The host drives panel, reached the way a person reaches it (the machine page's ⋯ menu, Host drives…): it draws a row
/// for every drive of this computer, offers writing on none that this computer uses, and closes on Escape. It lists the
/// real drives and changes nothing on any of them.
/// </summary>
public sealed class DrivesPanelTests
{
    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task The_host_drives_panel_lists_this_computers_drives_and_guards_the_system_one()
    {
        using var profile = new TestProfile("drives-panel");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Off");
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig { Name = "Off", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
        var drives = HostDrives.List();
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, window.GetLogicalDescendants().OfType<ListBox>().First(l => l.Name == "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected is not null, "the machine page");
            Ui.Pump(0.3);
            var more = window.GetVisualDescendants().OfType<MachineView>().Single().GetLogicalDescendants().OfType<Button>().First(b => b.Name == "More");
            Ui.Click(window, more);
            MenuItem? item = null;
            Ui.PumpUntil(() => (item = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().FirstOrDefault()?.GetRealizedContainers().OfType<MenuItem>().FirstOrDefault(m => m.Name == "OpenDrivesItem")) is not null, "the ⋯ menu");
            Ui.Click(window, item!);
            Ui.PumpUntil(() => shell.DrivesPanel is { Drives.Count: > 0 } || (shell.DrivesPanel is { } p && p.Note.Length > 0), "the drives panel");
            Ui.Pump(0.5);
            var rows = window.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "HostDriveRows").GetRealizedContainers().Count();
            var panel = shell.DrivesPanel!;
            var systemWritable = panel.Drives.Where(r => r.Drive.HoldsSystem).Select(r => r.CanWrite).ToList();
            Ui.Capture(window, "phase7", "drives-panel-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "phase7", "drives-panel-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Focus();
            window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
            Ui.PumpUntil(() => shell.DrivesPanel is null, "Escape to close the panel");
            window.Close();
            return (rows, systemWritable, panel.Drives.Count);
        });

        Assert.Equal(drives.Count, seen.Count);
        Assert.Equal(drives.Count, seen.rows);
        Assert.All(seen.systemWritable, Assert.False);
    }
}
