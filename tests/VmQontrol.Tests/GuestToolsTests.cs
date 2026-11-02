using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>The guest tools helper: the right install steps per system, and the card on the machine page that shows them.</summary>
public sealed class GuestToolsTests
{
    [Theory]
    [InlineData("http://ubuntu.com/ubuntu/24.04", "sudo apt install")]
    [InlineData("http://debian.org/debian/13", "sudo apt install")]
    [InlineData("http://fedoraproject.org/fedora/44", "sudo dnf install")]
    [InlineData("http://archlinux.org/archlinux/rolling", "sudo pacman -S")]
    [InlineData("http://opensuse.org/opensuse/tumbleweed", "sudo zypper install")]
    [InlineData("http://alpinelinux.org/alpinelinux/3.22", "doas apk add")]
    [InlineData(null, "with the system's package manager")]
    public void Linux_gets_its_own_package_managers_command(string? osInfo, string expected)
    {
        var advice = GuestTools.Advise(new GuestConfig { Os = "linux", OsInfoId = osInfo });
        Assert.False(advice.NeedsDriversDisc);
        Assert.Contains(expected, advice.Command);
        Assert.Contains("qemu-guest-agent", advice.Command);
        Assert.Contains("spice-vdagent", advice.Command);
        Assert.Contains(advice.AfterSteps, s => s.StartsWith("Log out", StringComparison.Ordinal));
    }

    [Fact]
    public void The_guests_own_name_beats_the_catalogues_guess()
    {
        var advice = GuestTools.Advise(new GuestConfig { Os = "linux", OsInfoId = "http://ubuntu.com/ubuntu/24.04" }, osReleaseId: "fedora");
        Assert.Contains("sudo dnf install", advice.Command);
    }

    [Fact]
    public void Windows_runs_the_installer_from_the_driver_disc()
    {
        var advice = GuestTools.Advise(new GuestConfig { Os = "windows", Release = "11" });
        Assert.True(advice.NeedsDriversDisc);
        Assert.Null(advice.Command);
        Assert.Contains(advice.Steps, s => s.Contains("virtio-win-guest-tools.exe", StringComparison.Ordinal));
        Assert.Empty(advice.AfterSteps);
    }

    [Fact]
    public void The_machines_own_driver_disc_is_found_beside_it()
    {
        using var profile = new TestProfile("guest-tools-disc");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "W");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "W", Media = [new MediaConfig { Role = MediaRole.Drivers, File = "virtio-win.iso" }] };
        Assert.Null(GuestTools.MachineDriversDisc(m, dir)); // named but missing: not offered
        File.WriteAllBytes(Path.Combine(dir, "virtio-win.iso"), [0]);
        Assert.Equal(Path.Combine(dir, "virtio-win.iso"), GuestTools.MachineDriversDisc(m, dir));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_driver_disc_given_to_an_off_machine_goes_in_at_power_on_and_is_remembered()
    {
        using var profile = new TestProfile("guest-tools-insert");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Win");
        Directory.CreateDirectory(dir);
        var iso = Path.Combine(profile.Root, "virtio-win.iso");
        File.WriteAllBytes(iso, [0]);
        MachineStore.Save(new MachineConfig { Name = "Win", Guest = new GuestConfig { Os = "windows", Release = "11" }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
        var machine = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            return shell.Machines[0];
        });
        await machine.InsertDriversDiscAsync(iso);
        var known = await Ui.Run(() => { Ui.PumpUntil(() => machine.KnownDriversDisc is not null, "the disc on the machine"); return machine.KnownDriversDisc; });
        Assert.Equal(iso, known);
        var saved = MachineStore.Load(Path.Combine(dir, "vm.json"));
        Assert.Contains(saved.Media, x => x.Role == MediaRole.Drivers && x.File == iso && x.Connect == Connect.Always);
        for (var i = 0; i < 50 && AppSettings.Load(profile.Paths).DriversIso is null; i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(iso, AppSettings.Load(profile.Paths).DriversIso);
    }

    [Trait("Tier", "Window")]
    [Theory]
    [InlineData("linux", "light")]
    [InlineData("linux", "dark")]
    [InlineData("windows", "dark")]
    [InlineData("windows", "light")]
    public async Task The_machine_page_shows_how_to_install_the_guest_tools(string os, string theme)
    {
        using var profile = new TestProfile($"guest-tools-{os}-{theme}");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Box");
        Directory.CreateDirectory(dir);
        var guest = os == "windows" ? new GuestConfig { Os = "windows", Release = "11" } : new GuestConfig { Os = "linux", OsInfoId = "http://ubuntu.com/ubuntu/24.04" };
        MachineStore.Save(new MachineConfig { Name = "Box", Guest = guest, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 1000 };
            Avalonia.Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, window.GetLogicalDescendants().OfType<ListBox>().First(l => l.Name == "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected is not null, "the machine page");
            Ui.Pump(0.3);
            var page = window.GetVisualDescendants().OfType<MachineView>().Single();
            var steps = page.GetLogicalDescendants().OfType<StackPanel>().First(p => p.Name == "GuestToolsSteps");
            var hiddenAtFirst = !steps.IsEffectivelyVisible;
            Ui.Click(window, page.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "GuestToolsHow"));
            Ui.PumpUntil(() => steps.IsEffectivelyVisible && steps.Bounds.Height > 40, "the steps");
            var card = page.GetLogicalDescendants().OfType<Border>().First(b => b.Name == "GuestToolsCard");
            card.BringIntoView();
            Ui.Pump(0.3);
            Ui.Capture(window, "phase7", $"guest-tools-{os}-{theme}");
            var command = page.GetLogicalDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "GuestToolsCommand");
            var insert = page.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "InsertDriversDisc");
            var result = (hiddenAtFirst, Command: command.IsEffectivelyVisible ? command.Text : null, Insert: insert.IsEffectivelyVisible && insert.Bounds.Width > 20,
                CardWidth: card.Bounds.Width);
            window.Close();
            return result;
        });

        Assert.True(seen.hiddenAtFirst);
        Assert.InRange(seen.CardWidth, 300, 1200);
        if (os == "windows")
        {
            Assert.True(seen.Insert);
            Assert.Null(seen.Command);
        }
        else
        {
            Assert.False(seen.Insert);
            Assert.Contains("sudo apt install", seen.Command);
        }
    }
}
