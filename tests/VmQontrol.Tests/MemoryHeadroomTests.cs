using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Phase 12: the memory check before Power on, and Preferences' defaults for new machines.</summary>
public sealed class MemoryHeadroomTests
{
    private const string Meminfo = "MemTotal:       63389236 kB\nMemFree:         1000000 kB\nMemAvailable:   35651584 kB\nBuffers:            1234 kB\n";

    [Fact]
    public void Meminfo_is_read_in_MiB()
    {
        var (total, available) = MemoryHeadroom.Read(Meminfo);
        Assert.Equal(61903, total);
        Assert.Equal(34816, available);
        Assert.Equal((0L, 0L), MemoryHeadroom.Read("nothing here"));
    }

    [Fact]
    public void Automatic_keeps_an_eighth_between_4_and_16_GB()
    {
        Assert.Equal(4096, MemoryHeadroom.AutomaticKeepFreeMiB(8192));
        Assert.Equal(7737, MemoryHeadroom.AutomaticKeepFreeMiB(61903));
        Assert.Equal(16384, MemoryHeadroom.AutomaticKeepFreeMiB(512 * 1024));
        Assert.Equal(8192, new AppSettings { KeepFreeGB = 8 }.KeepFreeMiB(61903));
        Assert.Equal(7737, new AppSettings().KeepFreeMiB(61903));
    }

    [Fact]
    public void The_check_counts_what_running_machines_can_still_take()
    {
        // 34 GB free, keep 8: a 16 GB machine fits alone.
        Assert.Null(MemoryHeadroom.Check(16384, 34816, 0, 8192));
        // A running machine that has touched 2 of its 16 GB can still take 14: now it does not fit.
        var promised = MemoryHeadroom.Promised(16384, 2048);
        Assert.Equal(14336, promised);
        var s = MemoryHeadroom.Check(16384, 34816, promised, 8192);
        Assert.NotNull(s);
        Assert.Equal(4096, s.AfterMiB);
        var words = string.Join(" ", MemoryHeadroom.Describe(s, "Fedora"));
        Assert.Contains("Fedora needs 16 GB", words, StringComparison.Ordinal);
        Assert.Contains("34 GB free now", words, StringComparison.Ordinal);
        Assert.Contains("can still take 14 GB", words, StringComparison.Ordinal);
        Assert.Contains("leaves 4 GB, less than the 8 GB", words, StringComparison.Ordinal);
        // A machine that holds more than it was given (QEMU's own overhead) promises nothing more.
        Assert.Equal(0, MemoryHeadroom.Promised(4096, 4400));
        // Unknown memory never warns.
        Assert.Null(MemoryHeadroom.Check(16384, 0, 0, 8192));
        Assert.Contains("not enough memory", string.Join(" ", MemoryHeadroom.Describe(MemoryHeadroom.Check(65536, 34816, 0, 8192)!, "X")), StringComparison.Ordinal);
    }

    [Fact]
    public void This_process_has_a_resident_size_and_a_gone_one_has_none()
    {
        Assert.True(MemoryHeadroom.ResidentMiB(Environment.ProcessId) > 0);
        Assert.Null(MemoryHeadroom.ResidentMiB(int.MaxValue));
        Assert.True(MemoryHeadroom.ReadHost().TotalMiB > 0);
    }

    [Theory]
    [InlineData(64, 32)]
    [InlineData(16, 8)]
    public void Sizes_step_either_side_of_the_recommendation(int hostGb, int cpus)
    {
        var host = RecordedHost.Load() with { MemoryGB = hostGb, LogicalCpus = cpus };
        var guest = new GuestConfig { Os = "linux" };
        var light = Recommendations.For(guest, null, host, size: MachineSize.Lighter);
        var normal = Recommendations.For(guest, null, host);
        var big = Recommendations.For(guest, null, host, size: MachineSize.Generous);
        Assert.True(light.MemoryMiB.Value < normal.MemoryMiB.Value && normal.MemoryMiB.Value < big.MemoryMiB.Value, $"{light.MemoryMiB.Value} {normal.MemoryMiB.Value} {big.MemoryMiB.Value}");
        Assert.True(light.Cores.Value < normal.Cores.Value && normal.Cores.Value < big.Cores.Value, $"{light.Cores.Value} {normal.Cores.Value} {big.Cores.Value}");
        Assert.True(light.MemoryMiB.Value <= hostGb * 1024L / 4);
        Assert.True(big.MemoryMiB.Value <= hostGb * 1024L * 3 / 4);
        Assert.True(big.Cores.Value <= cpus * 3 / 4);
        Assert.Equal(normal.MemoryMiB.Value, Recommendations.QuickemuMemoryMiB(hostGb) is var q && q <= hostGb * 512L ? q : hostGb * 512L);
    }

    [Fact]
    public void Lighter_never_goes_below_what_the_system_needs()
    {
        var host = RecordedHost.Load() with { MemoryGB = 8, LogicalCpus = 4 };
        var r = Recommendations.For(new GuestConfig { Os = "windows", Release = "11" }, null, host, size: MachineSize.Lighter);
        Assert.Equal(4096, r.MemoryMiB.Value);
        Assert.Equal(2, r.Cores.Value);
    }

    [Fact]
    public void The_new_settings_are_kept_and_the_size_is_not_written_twice()
    {
        using var profile = new TestProfile("memory-settings");
        new AppSettings { LibraryRoots = [profile.Paths.DefaultLibrary], KeepFreeGB = 16, WarnLowMemory = false, NewMachineSize = "generous", NewMachinePowerOn = false, NewMachineLean = false }.Save(profile.Paths);
        var loaded = AppSettings.Load(profile.Paths);
        Assert.Equal(16, loaded.KeepFreeGB);
        Assert.False(loaded.WarnLowMemory);
        Assert.Equal(MachineSize.Generous, loaded.Size);
        Assert.False(loaded.NewMachinePowerOn);
        Assert.False(loaded.NewMachineLean);
        Assert.DoesNotContain("\"size\"", File.ReadAllText(profile.Paths.SettingsFile), StringComparison.Ordinal);
        // An old settings file has none of them: the defaults apply.
        File.WriteAllText(profile.Paths.SettingsFile, "{\"libraryRoots\":[\"" + profile.Paths.DefaultLibrary + "\"]}");
        var old = AppSettings.Load(profile.Paths);
        Assert.True(old.WarnLowMemory && old.NewMachinePowerOn && old.NewMachineLean);
        Assert.Null(old.KeepFreeGB);
        Assert.Equal(MachineSize.Recommended, old.Size);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task Preferences_set_what_the_wizard_starts_with()
    {
        using var profile = new TestProfile("new-machine-defaults");
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.Pump(0.4);

            // The wizard before: recommended size, power on ticked, Lean on.
            shell.NewMachine();
            Ui.PumpUntil(() => shell.Wizard is { } w && w.MemoryReason.Length > 0, "the wizard's recommendation");
            var before = (shell.Wizard!.MemoryGb, shell.Wizard.PowerOnAfter, shell.Wizard.LeanWindows);
            shell.Wizard.CancelCommand.Execute(null);
            Ui.PumpUntil(() => shell.Wizard is null, "the wizard closed");

            Ui.Click(window, window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "OpenPreferences"));
            Ui.PumpUntil(() => shell.ShowPreferences, "the preferences page");
            Ui.Pump(0.3);
            var view = window.GetVisualDescendants().OfType<PreferencesView>().Single();
            var toggle = view.GetLogicalDescendants().OfType<ToggleSwitch>().First(t => t.Name == "NewPowerOn");
            toggle.BringIntoView();
            Ui.Pump(0.3);
            Ui.Click(window, toggle);
            Ui.PumpUntil(() => !shell.Settings.NewMachinePowerOn, "Power on when created, off");
            var size = view.GetLogicalDescendants().OfType<ComboBox>().First(c => c.Name == "SizeChoice");
            size.SelectedItem = PreferencesViewModel.SizeOptions.First(o => o.Value == "lighter");
            Ui.PumpUntil(() => shell.Settings.Size == MachineSize.Lighter, "lighter machines");
            var keep = view.GetLogicalDescendants().OfType<ComboBox>().First(c => c.Name == "KeepFreeChoice");
            keep.BringIntoView();
            keep.SelectedItem = PreferencesViewModel.KeepFreeOptions.First(o => o.Value == "16");
            Ui.PumpUntil(() => shell.Settings.KeepFreeGB == 16 && shell.PreferencesPage!.MemoryText.Contains("16 GB stays free", StringComparison.Ordinal), "16 GB kept free, said");
            Ui.Pump(0.3);
            Ui.Capture(window, "s8", "preferences-memory-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "s8", "preferences-memory-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            Ui.Click(window, view.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "ClosePreferences"));
            Ui.PumpUntil(() => !shell.ShowPreferences, "preferences closed");

            shell.NewMachine();
            Ui.PumpUntil(() => shell.Wizard is { } w && w.MemoryReason.Length > 0, "the wizard's recommendation, again");
            var after = (shell.Wizard!.MemoryGb, shell.Wizard.PowerOnAfter, shell.Wizard.LeanWindows);
            shell.Wizard.CancelCommand.Execute(null);
            window.Close();
            return (before, after);
        });

        Assert.True(seen.before.PowerOnAfter);
        Assert.True(seen.before.LeanWindows);
        Assert.False(seen.after.PowerOnAfter);
        Assert.True(seen.after.LeanWindows);
        Assert.True(seen.after.MemoryGb < seen.before.MemoryGb, $"{seen.after.MemoryGb} < {seen.before.MemoryGb}");
        var saved = AppSettings.Load(profile.Paths);
        Assert.Equal(16, saved.KeepFreeGB);
        Assert.Equal("lighter", saved.NewMachineSize);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task A_machine_too_big_for_this_computer_asks_first_and_Cancel_leaves_it_off()
    {
        using var profile = new TestProfile("memory-warning");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Too big");
        Directory.CreateDirectory(dir);
        // Far more than any computer running these tests has: the check must stop it.
        MachineStore.Save(new MachineConfig { Name = "Too big", MemoryMiB = 4L * 1024 * 1024, Guest = new GuestConfig { Os = "linux" }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] }, Path.Combine(dir, MachineStore.FileName));
        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            var machine = shell.Machines.Single();
            shell.Selected = machine;
            Ui.Pump(0.4);
            var power = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PowerOn" && b.IsEffectivelyVisible);
            Ui.Click(window, power);
            Ui.PumpUntil(() => shell.Notice is not null, "the memory warning", 20);
            Ui.Pump(0.3);
            Ui.Capture(window, "s8", "memory-warning-dark");
            var notice = shell.Notice!;
            var said = (notice.Title, Lines: string.Join(" ", notice.Lines), notice.ConfirmText);
            Ui.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "NoticeDismiss"));
            Ui.PumpUntil(() => shell.Status == "Too big was not started.", "Cancel answered", 10);
            Ui.PumpUntil(() => !machine.Busy, "the machine free again", 10);
            var state = machine.State;
            var running = machine.Running;
            var runtime = profile.Paths.RuntimeFor(machine.Machine.Id);
            window.Close();
            return (said, state, running, runtime);
        });

        Assert.Equal("Too big may not have enough memory", seen.said.Title);
        Assert.Contains("Too big needs 4096 GB of memory", seen.said.Lines, StringComparison.Ordinal);
        Assert.Contains("not enough memory", seen.said.Lines, StringComparison.Ordinal);
        Assert.Equal("Power on anyway", seen.said.ConfirmText);
        Assert.Equal(PowerState.Off, seen.state);
        Assert.Null(seen.running);
        // The control: nothing was started, so no runtime folder exists for it.
        Assert.False(Directory.Exists(seen.runtime));
    }
}
