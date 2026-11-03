using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// The phase 5 audit's four rows, through the window: Processors → Nested virtualization Off is saved; Hard disks →
/// Compact gives a filled disk's space back and says so; Boot and devices → vsock shows the machine's address.
/// </summary>
public sealed class AuditFiveSettingsTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task Nested_compact_and_vsock_work_from_the_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("audit5-settings");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Lab");
        Directory.CreateDirectory(dir);
        var disk = Path.Combine(dir, "disk0.qcow2");
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-o", "preallocation=full", disk, "64M");
        var before = (await QemuImg.InfoAsync(disk, ct))["actual-size"]!.GetValue<long>();
        MachineStore.Save(new MachineConfig { Name = "Lab", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, "vm.json"));

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

            void Both(string name)
            {
                Ui.Pump(0.4);
                Ui.Capture(window, "s9", name + "-dark");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Ui.Pump(0.3);
                Ui.Capture(window, "s9", name + "-light");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            }

            settings.Section = settings.Sections.First(s => s.Key == "processors");
            Ui.Pump(0.3);
            var nested = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "Nested" && c.IsEffectivelyVisible);
            var nestedDefault = (nested.SelectedItem as Option)?.Label;
            nested.SelectedItem = SettingsViewModel.NestedOptions.First(o => o.Value == "off");
            Both("processors-nested");

            settings.Section = settings.Sections.First(s => s.Key == "disks");
            Ui.Pump(0.3);
            var compact = window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Compact" && b.IsEffectivelyVisible);
            Ui.Click(window, compact);
            Ui.PumpUntil(() => settings.Disks[0].CompactStatus.StartsWith("Compacted", StringComparison.Ordinal), "the compact", 20);
            var compactStatus = settings.Disks[0].CompactStatus;
            Both("disks-compact");

            settings.Section = settings.Sections.First(s => s.Key == "boot");
            settings.Vsock = true;
            Ui.Pump(0.3);
            var vsockShown = window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text?.StartsWith("This machine's address:", StringComparison.Ordinal) == true);
            var row = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "VsockRow");
            row.BringIntoView();
            Both("devices-vsock");

            settings.SaveCommand.Execute(null);
            Ui.PumpUntil(() => MachineStore.Load(Path.Combine(dir, "vm.json")).Devices.Vsock, "the save", 10);
            window.Close();
            return (nestedDefault, compactStatus, vsockShown);
        });

        Assert.Equal("As this computer allows (recommended)", seen.nestedDefault);
        Assert.StartsWith("Compacted: ", seen.compactStatus, StringComparison.Ordinal);
        Assert.True(seen.vsockShown, "the vsock address line is not shown");
        var after = (await QemuImg.InfoAsync(disk, ct))["actual-size"]!.GetValue<long>();
        Assert.True(after < before / 4, $"the disk still takes {after} of {before} bytes");
        var saved = MachineStore.Load(Path.Combine(dir, "vm.json"));
        Assert.False(saved.Cpu.Nested);
        Assert.True(saved.Devices.Vsock);
    }
}
