using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

/// <summary>
/// Settings → Performance on this computer: the recommended pinning is offered for its real topology, choosing it and
/// saving writes those processors, and the PCI list shows this computer's devices with the ones it needs marked. Nothing
/// on the host is changed (no Reserve, no device handed over).
/// </summary>
public sealed class PerformanceSettingsTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task The_recommended_pinning_is_saved_and_host_devices_are_marked()
    {
        using var profile = new TestProfile("perf-settings");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Game");
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig { Name = "Game", Guest = new GuestConfig { Os = "windows" }, Cpu = new CpuConfig { Cores = 4 }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));
        var expected = CpuTopology.Recommend(CpuTopology.List(), 4);
        var seen = await Ui.Run(() =>
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
            settings.Section = settings.Sections.First(s => s.Key == "performance");
            Ui.PumpUntil(() => settings.Perf.PinChoices.Count > 1, "the host's topology");
            Ui.Pump(0.4);
            var recommended = settings.Perf.PinChoices.FirstOrDefault(c => c.Title.StartsWith("Recommended", StringComparison.Ordinal));
            if (recommended is not null) settings.Perf.Pin = recommended;
            Ui.Pump(0.2);
            Ui.Capture(window, "phase10", "performance-dark");
            var pci = window.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "PciRows");
            pci.BringIntoView();
            Ui.Pump(0.4);
            Ui.Capture(window, "phase10", "performance-pci-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "phase10", "performance-pci-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            var screenRow = settings.Perf.PciDevices.FirstOrDefault(r => r.Device.HostUse == "it shows this computer's screen");
            var marked = (screenRow?.CanChoose, screenRow?.HasWarning);
            settings.SaveCommand.Execute(null);
            Ui.PumpUntil(() => shell.SettingsPage is null || shell.Selected!.Machine.Performance.PinnedCpus.Count > 0, "the save", 10);
            window.Close();
            return (recommended?.Title, marked);
        });

        if (expected is not null)
        {
            Assert.NotNull(seen.Title);
            var saved = MachineStore.Load(Path.Combine(dir, "vm.json"));
            Assert.Equal(expected, saved.Performance.PinnedCpus);
            Assert.NotEmpty(saved.Performance.EmulatorCpus);
            Assert.DoesNotContain(saved.Performance.EmulatorCpus, saved.Performance.PinnedCpus.Contains);
        }

        if (seen.marked.CanChoose is not null)
        {
            Assert.False(seen.marked.CanChoose, "the screen's graphics card is not offered");
            Assert.True(seen.marked.HasWarning);
        }
    }
}
