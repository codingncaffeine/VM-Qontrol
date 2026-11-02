using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

/// <summary>
/// The toolbar's USB menu as it is DRAWN: the presenter on screen (not the flyout's item list, which once held every
/// device while the presenter drew an empty 2-pixel sliver) shows every device, at a usable size, scrolling when long.
/// It lists this computer's real devices and clicks none of them.
/// </summary>
public sealed class UsbMenuTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_USB_menu_draws_every_device_plugged_in()
    {
        using var profile = new TestProfile("usb-menu");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Off");
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig { Name = "Off", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
        var plugged = VmQontrol.Core.Devices.UsbHost.List().Count;
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
            var button = window.GetVisualDescendants().OfType<MachineView>().Single().GetLogicalDescendants().OfType<Button>().First(b => b.Name == "OpenUsb");
            Ui.Click(window, button);
            MenuFlyoutPresenter? presenter = null;
            Ui.PumpUntil(() => (presenter = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().FirstOrDefault()) is { ItemCount: > 1 }
                && presenter.GetRealizedContainers().OfType<MenuItem>().Any(m => m.Name == "ManageUsb"), "the drawn menu", 10);
            Ui.Pump(0.3);
            Ui.Capture(window, "phase7", "usb-menu-open");
            var result = (presenter!.ItemCount, presenter.Bounds.Width, presenter.Bounds.Height,
                Disabled: presenter.GetRealizedContainers().OfType<MenuItem>().Count(m => m.Header is StackPanel && !m.IsEffectivelyEnabled));
            ((MenuFlyout)button.Flyout!).Hide();
            window.Close();
            return result;
        });

        // Every device, plus "Power the machine on…" (it is off), the separator and Manage….
        Assert.Equal(plugged + 3 + (plugged == 0 ? 1 : 0), seen.ItemCount);
        Assert.InRange(seen.Width, 240, 700);
        Assert.InRange(seen.Height, 40, 520);
        Assert.Equal(plugged, seen.Disabled); // off: listed, not clickable
    }
}
