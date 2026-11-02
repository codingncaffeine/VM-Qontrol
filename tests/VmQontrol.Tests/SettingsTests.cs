using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Library;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void Changes_are_described_in_words()
    {
        var before = new MachineConfig { Name = "a" };
        var after = before with { MemoryMiB = 6144, Tpm = true, Cpu = new CpuConfig { Cores = 4 } };
        Assert.Equal(["Memory: recommended → 6 GB", "Processors: recommended → 4", "TPM: off → on"], SettingsHistory.Describe(before, after));
        Assert.Empty(SettingsHistory.Describe(before, before with { }));
    }

    [Fact]
    public void An_AutoProtect_change_alone_is_saved()
    {
        using var profile = new TestProfile("history-autoprotect");
        var file = Path.Combine(profile.Root, "vm.json");
        var before = new MachineConfig { Name = "a" };
        MachineStore.Save(before, file);
        var changes = SettingsHistory.Save(before, before with { AutoProtect = new AutoProtectConfig { Enabled = true, IntervalMinutes = 30, Keep = 5 } }, file, profile.Root);
        Assert.Equal(["AutoProtect: off → every 30 min, keep 5"], changes);
        Assert.True(MachineStore.Load(file).AutoProtect.Enabled);
    }

    [Fact]
    public void Saving_records_the_change_and_the_settings_before_it()
    {
        using var profile = new TestProfile("history");
        var file = Path.Combine(profile.Root, "vm.json");
        var before = new MachineConfig { Name = "a", MemoryMiB = 2048 };
        MachineStore.Save(before, file);
        SettingsHistory.Save(before, before with { MemoryMiB = 4096 }, file, profile.Root);
        Assert.Equal(4096, MachineStore.Load(file).MemoryMiB);
        var change = Assert.Single(SettingsHistory.Read(profile.Root));
        Assert.Equal(2048, SettingsHistory.Before(change).MemoryMiB);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AutoProtect_turned_on_in_settings_is_saved()
    {
        using var profile = new TestProfile("settings-autoprotect");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Box");
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig { Name = "Box", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 20 }] }, Path.Combine(dir, "vm.json"));

        var described = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected is not null, "the machine page");
            Ui.Pump(0.3);
            Ui.Click(window, Named<Button>(window.GetVisualDescendants().OfType<MachineView>().Single(), "OpenSettings"));
            Ui.PumpUntil(() => shell.SettingsPage is not null, "the settings", 15);
            Ui.Pump(0.3);
            var settings = shell.SettingsPage!;
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            Ui.Click(window, Named<ListBox>(view, "Sections").GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as SettingsSection)?.Key == "autoprotect"));
            Ui.Pump(0.2);
            Ui.Click(window, Named<CheckBox>(view, "AutoProtectOn"));
            Ui.Pump(0.2);
            Ui.Capture(window, "phase6", "settings-autoprotect");
            Ui.Click(window, Named<Button>(view, "Save"));
            Ui.PumpUntil(() => settings.SaveNote.StartsWith("Saved", StringComparison.Ordinal), "the save");
            window.Close();
            return settings.SaveNote;
        });

        var saved = MachineStore.Load(Path.Combine(dir, "vm.json")).AutoProtect;
        Assert.True(saved.Enabled, described);
        Assert.Equal(60, saved.IntervalMinutes);
        Assert.Equal(3, saved.Keep);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task Memory_changed_in_settings_is_saved_recorded_and_restored()
    {
        using var profile = new TestProfile("settings");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Box");
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig { Name = "Box", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 20 }] }, Path.Combine(dir, "vm.json"));

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected is not null, "the machine page");
            Ui.Pump(0.3);
            Ui.Click(window, Named<Button>(window.GetVisualDescendants().OfType<MachineView>().Single(), "OpenSettings"));
            Ui.PumpUntil(() => shell.SettingsPage is not null, "the settings", 15);
            Ui.Pump(0.3);
            var settings = shell.SettingsPage!;
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var memoryItem = Named<ListBox>(view, "Sections").GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as SettingsSection)?.Key == "memory");
            Ui.Click(window, memoryItem);
            Ui.Pump(0.2);
            settings.MemoryAuto = false;
            settings.MemoryGb = 6;
            Ui.Capture(window, "phase5", "settings-memory");
            Ui.Click(window, Named<Button>(view, "Save"));
            Ui.PumpUntil(() => settings.SaveNote.StartsWith("Saved", StringComparison.Ordinal), "the save");
            var saved = MachineStore.Load(Path.Combine(dir, "vm.json")).MemoryMiB;
            var historyItem = Named<ListBox>(view, "Sections").GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as SettingsSection)?.Key == "history");
            Ui.Click(window, historyItem);
            Ui.PumpUntil(() => settings.History.Count == 1, "the change in the history");
            Ui.Pump(0.2);
            Ui.Capture(window, "phase5", "settings-history");
            var described = string.Join("; ", settings.History[0].Changes);
            settings.RestoreCommand.Execute(settings.History[0]);
            Ui.PumpUntil(() => shell.SettingsPage is null, "the settings to close after restoring");
            var restored = MachineStore.Load(Path.Combine(dir, "vm.json")).MemoryMiB;
            var shown = shell.Selected!.Details.First(d => d.Label == "Memory").Value;
            window.Close();
            return (saved, described, restored, shown);
        });

        Assert.Equal(6144, seen.saved);
        Assert.Equal("Memory: recommended → 6 GB", seen.described);
        Assert.Null(seen.restored);
        Assert.Contains("recommended", seen.shown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quickemu_machine_takes_its_settings_from_VM_Qontrol_while_its_conf_stays_untouched()
    {
        using var profile = new TestProfile("qsettings");
        Directory.CreateDirectory(profile.Paths.DefaultLibrary);
        var conf = Path.Combine(profile.Paths.DefaultLibrary, "linux.conf");
        File.Copy(Path.Combine(RecordedHost.FixtureDirectory, "linux.conf"), conf);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(conf)));
        var entry = LibraryScanner.Scan(profile.Paths.DefaultLibrary, profile.Paths.State).Single();
        var historyDir = Path.Combine(profile.Paths.State, "machines", entry.Machine!.Id);
        SettingsHistory.Save(entry.Machine, entry.Machine with { MemoryMiB = 3072 }, Path.Combine(historyDir, "vm.json"), historyDir);

        var again = LibraryScanner.Scan(profile.Paths.DefaultLibrary, profile.Paths.State).Single();
        Assert.Equal(3072, again.Machine!.MemoryMiB);
        Assert.Equal(entry.Machine.Id, again.Machine.Id);
        Assert.Equal(MachineKind.Quickemu, again.Kind);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(conf))));
    }
}
