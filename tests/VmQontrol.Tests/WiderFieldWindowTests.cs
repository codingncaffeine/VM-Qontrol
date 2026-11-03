using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// Phase 17a from the window, the way a person does it: ⋯ → Disconnect the network cable shows "Cable out" beside the
/// state and its plug button puts it back; ⋯ → Capture network traffic shows its mark, whose stop button finishes the
/// .pcap in the machine's folder. Screens captured dark and light with both marks showing.
/// </summary>
public sealed class WiderFieldWindowTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task Cable_and_capture_from_the_menu_and_the_marks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("cable-ui");
        Assert.True(profile.Paths.Isolated);
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Net");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "Net", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        MachineStore.Save(m, Path.Combine(dir, "vm.json"));
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            var seen = await Ui.Run(() =>
            {
                var shell = new VmQontrol.App.ViewModels.ShellViewModel(profile.Paths, VmQontrol.Core.AppSettings.Load(profile.Paths)) { WatchUsb = false };
                var window = new VmQontrol.App.Views.MainWindow { DataContext = shell, Width = 1320, Height = 860 };
                Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                window.Show();
                _ = shell.StartAsync();
                Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
                Ui.Click(window, window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
                Ui.PumpUntil(() => shell.Selected is { IsRunning: true }, "the running machine", 20);
                Ui.Pump(0.3);
                var page = window.GetVisualDescendants().OfType<VmQontrol.App.Views.MachineView>().Single();

                void Menu(string name)
                {
                    Ui.Click(window, page.GetVisualDescendants().OfType<Button>().First(b => b.Name == "More"));
                    MenuItem? item = null;
                    Ui.PumpUntil(() => (item = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().FirstOrDefault()?.GetRealizedContainers()
                        .OfType<MenuItem>().FirstOrDefault(i => i.Name == name && i.IsVisible)) is not null, $"{name} in the ⋯ menu");
                    Ui.Click(window, item!);
                    Ui.Pump(0.2);
                }

                Border Pill(string name) => page.GetVisualDescendants().OfType<Border>().First(b => b.Name == name);

                var vm = shell.Selected!;
                var pillHiddenFirst = !Pill("CablePill").IsEffectivelyVisible && !Pill("CapturePill").IsEffectivelyVisible;
                Menu("DisconnectCableItem");
                Ui.PumpUntil(() => vm.CableOut, "the cable to come out", 10);
                Menu("CaptureItem");
                Ui.PumpUntil(() => vm.IsCapturing, "the capture to start", 10);
                Ui.Pump(0.3);
                var bothShown = Pill("CablePill").IsEffectivelyVisible && Pill("CapturePill").IsEffectivelyVisible;
                var marker = LiveNetwork.IsUnplugged(vm.Running!.Paths);
                Ui.Capture(window, "phase17", "cable-capture-dark");
                Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
                Ui.Pump(0.3);
                Ui.Capture(window, "phase17", "cable-capture-light");
                Ui.Click(window, page.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PlugCable"));
                Ui.PumpUntil(() => !vm.CableOut, "the cable to go back in", 10);
                Ui.Click(window, page.GetVisualDescendants().OfType<Button>().First(b => b.Name == "StopCapture"));
                Ui.PumpUntil(() => !vm.IsCapturing, "the capture to finish", 10);
                Ui.Pump(0.3);
                var hiddenAfter = !Pill("CablePill").IsEffectivelyVisible && !Pill("CapturePill").IsEffectivelyVisible;
                var unpluggedAfter = LiveNetwork.IsUnplugged(vm.Running!.Paths);
                window.Close();
                return (pillHiddenFirst, bothShown, marker, hiddenAfter, unpluggedAfter);
            });

            Assert.True(seen.pillHiddenFirst, "no marks before anything was chosen");
            Assert.True(seen.bothShown, "Cable out and Capturing show beside the state");
            Assert.True(seen.marker, "the cable-out marker is in the runtime folder");
            Assert.True(seen.hiddenAfter, "both marks go away");
            Assert.False(seen.unpluggedAfter);
            var pcap = Assert.Single(Directory.GetFiles(Path.Combine(dir, "captures"), "*.pcap"));
            var head = new byte[4];
            await using (var f = File.OpenRead(pcap)) Assert.Equal(4, await f.ReadAsync(head, ct));
            Assert.Equal(0xa1b2c3d4u, BitConverter.ToUInt32(head));
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task Disk_speed_limit_and_cable_at_power_on_are_saved_from_the_settings()
    {
        using var profile = new TestProfile("limits-settings");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Lab");
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig { Name = "Lab", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));

        await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected is not null, "the machine page");
            Ui.Click(window, window.GetVisualDescendants().OfType<MachineView>().Single().GetVisualDescendants().OfType<Button>().First(b => b.Name == "OpenSettings"));
            Ui.PumpUntil(() => shell.SettingsPage is not null, "the settings");
            var settings = shell.SettingsPage!;

            void Both(string name, Control show)
            {
                show.BringIntoView();
                Ui.Pump(0.4);
                Ui.Capture(window, "phase17", name + "-dark");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Ui.Pump(0.3);
                Ui.Capture(window, "phase17", name + "-light");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            }

            settings.Section = settings.Sections.First(s => s.Key == "disks");
            Ui.Pump(0.3);
            var mbps = window.GetVisualDescendants().OfType<NumericUpDown>().First(n => n.Name == "DiskLimitMBps" && n.IsEffectivelyVisible);
            mbps.Value = 50;
            Both("disk-speed-limit", mbps);

            settings.Section = settings.Sections.First(s => s.Key == "network");
            Ui.Pump(0.3);
            var cable = window.GetVisualDescendants().OfType<ToggleSwitch>().First(t => t.Name == "ConnectedAtPowerOn" && t.IsEffectivelyVisible);
            Assert.True(cable.IsChecked);
            Ui.Click(window, cable);
            Both("cable-at-power-on", cable);

            settings.SaveCommand.Execute(null);
            Ui.PumpUntil(() => MachineStore.Load(Path.Combine(dir, "vm.json")).Disks[0].LimitMBps == 50, "the save", 10);
            window.Close();
            return 0;
        });

        var saved = MachineStore.Load(Path.Combine(dir, "vm.json"));
        Assert.Equal(50, saved.Disks[0].LimitMBps);
        Assert.Null(saved.Disks[0].LimitIops);
        Assert.False(saved.Nics[0].ConnectedAtPowerOn);
    }
}
