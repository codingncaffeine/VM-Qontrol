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

    /// <summary>
    /// Every display field is in the description: a save whose description is empty writes nothing, so a field left out
    /// is a setting that can never be saved on its own (found 2026-11-02 for the viewer and the resolution).
    /// </summary>
    [Fact]
    public void Display_changes_on_their_own_are_described()
    {
        var before = new MachineConfig { Name = "a" };
        Assert.Equal(["Viewer program: auto → spicy"], SettingsHistory.Describe(before, before with { Display = before.Display with { Viewer = "spicy" } }));
        Assert.Equal(["Starting resolution: automatic → 1920 × 1080"], SettingsHistory.Describe(before, before with { Display = before.Display with { Width = 1920, Height = 1080 } }));
        Assert.Equal(["Full screen: off → on"], SettingsHistory.Describe(before, before with { Display = before.Display with { FullScreen = true } }));
        Assert.Equal(["Copy and paste: on → off"], SettingsHistory.Describe(before, before with { Display = before.Display with { CopyPaste = false } }));
        Assert.Equal(["Drag files in: on → off"], SettingsHistory.Describe(before, before with { Display = before.Display with { FileTransfer = false } }));
    }

    [Fact]
    public void Full_screen_is_passed_to_either_viewer()
    {
        using var profile = new TestProfile("viewer-args");
        var running = new VmQontrol.Core.Runtime.RunningMachine("id", "Box", 1, profile.Root, profile.Root, DateTimeOffset.Now, null);
        foreach (var name in new[] { "spicy", "remote-viewer" })
        {
            var program = Path.Combine(profile.Root, name);
            File.WriteAllText(program, "");
            var m = new MachineConfig { Name = "Box", Display = new DisplayConfig { Viewer = program, FullScreen = true } };
            Assert.Contains("--full-screen", VmQontrol.Core.Runtime.ViewerLauncher.Command(m, running)!.Value.Args);
            Assert.DoesNotContain("--full-screen", VmQontrol.Core.Runtime.ViewerLauncher.Command(m with { Display = m.Display with { FullScreen = false } }, running)!.Value.Args);
        }
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
    public async Task Adding_processors_while_running_is_turned_on_in_settings_and_saved()
    {
        using var profile = new TestProfile("settings-hotplug");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Box");
        Directory.CreateDirectory(dir);
        MachineStore.Save(new MachineConfig { Name = "Box", Cpu = new CpuConfig { Cores = 2 }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 20 }] }, Path.Combine(dir, "vm.json"));

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
            Ui.Click(window, Named<ListBox>(view, "Sections").GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as SettingsSection)?.Key == "processors"));
            Ui.Pump(0.2);
            var hidden = Named<NumericUpDown>(view, "MaxCores").IsEffectivelyVisible;
            Ui.Click(window, Named<ToggleSwitch>(view, "HotPlugCpus"));
            Ui.Pump(0.2);
            var shown = Named<NumericUpDown>(view, "MaxCores").IsEffectivelyVisible;
            Ui.Capture(window, "s8", "settings-processors-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.2);
            Ui.Capture(window, "s8", "settings-processors-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            Ui.Click(window, Named<Button>(view, "Save"));
            Ui.PumpUntil(() => settings.SaveNote.StartsWith("Saved", StringComparison.Ordinal), "the save");
            window.Close();
            return (settings.SaveNote, hidden, shown);
        });

        Assert.False(described.hidden);
        Assert.True(described.shown);
        var saved = MachineStore.Load(Path.Combine(dir, "vm.json")).Cpu;
        Assert.Equal(2, saved.Cores);
        // The offered ceiling is twice the cores, capped at this computer's processors (Environment.ProcessorCount follows
        // the CPU affinity: the gate runs on two, a desktop on many).
        Assert.Equal(Math.Min(Environment.ProcessorCount, 4), saved.MaxCores);
    }

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
            Ui.Click(window, Named<ToggleSwitch>(view, "AutoProtectOn"));
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

    /// <summary>
    /// Every section of Settings, captured in dark and light for a look (snapshots/settings-pages/). A machine with a
    /// disk, a disc, a share, a USB rule and a port forward, so the list sections show their rows. The check: every
    /// section shows exactly one visible page, and walking them all writes nothing to the machine.
    /// </summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task Every_settings_section_shows_its_page_in_both_themes()
    {
        using var profile = new TestProfile("settings-pages");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Workstation");
        Directory.CreateDirectory(dir);
        var share = Path.Combine(profile.Root, "Projects");
        Directory.CreateDirectory(share);
        MachineStore.Save(new MachineConfig
        {
            Name = "Workstation",
            Guest = new GuestConfig { Os = "linux" },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 64 }],
            Media = [new MediaConfig { Role = MediaRole.Installer, File = Path.Combine(profile.Root, "ubuntu-24.04-desktop-amd64.iso") }],
            Shares = [new ShareConfig { Path = share, Tag = "projects" }],
            Usb = new UsbConfig { Passthrough = [new UsbDevice("046d", "c52b", "Logitech USB Receiver")] },
            Nics = [new NicConfig { Forwards = [new PortForward("tcp", 8080, 80)] }],
            HostDrives = [new HostDriveConfig { Path = "/dev/disk/by-id/usb-Kingston_DataTraveler_3.0_E0D55EA5-0:0", Name = "Kingston DataTraveler 3.0 (removable disk, 28.9 GiB)", Writable = true }],
        }, Path.Combine(dir, "vm.json"));
        var before = File.ReadAllText(Path.Combine(dir, "vm.json"));

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths));
            var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
            Ui.PumpUntil(() => shell.Selected is not null, "the machine page");
            Ui.Pump(0.3);
            Ui.Click(window, Named<Button>(window.GetVisualDescendants().OfType<MachineView>().Single(), "OpenSettings"));
            Ui.PumpUntil(() => shell.SettingsPage is not null, "the settings", 15);
            Ui.Pump(0.3);
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var pages = new List<(string Key, int Visible)>();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = theme;
                foreach (var item in Named<ListBox>(view, "Sections").GetVisualDescendants().OfType<ListBoxItem>().ToList())
                {
                    var key = ((SettingsSection)item.DataContext!).Key;
                    Ui.Click(window, item);
                    Ui.Pump(0.15);
                    var visible = view.GetVisualDescendants().OfType<StackPanel>().Count(p => p.Classes.Contains("section") && p.IsVisible);
                    if (theme == ThemeVariant.Dark) pages.Add((key, visible));
                    Ui.Capture(window, "settings-pages", $"{(theme == ThemeVariant.Dark ? "dark" : "light")}-{key}");
                }
            }

            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Close();
            return pages;
        });

        Assert.Equal(19, seen.Count);
        Assert.All(seen, p => Assert.True(p.Visible == 1, $"{p.Key}: {p.Visible} pages visible"));
        Assert.Equal(before, File.ReadAllText(Path.Combine(dir, "vm.json")));
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

    /// <summary>
    /// The user's report (s7): "I can't eject either ISO". Once installed, a machine's installer and answer disc stay
    /// out of the drive, yet Settings listed them as if in it and Eject did nothing. Now each disc says where it is,
    /// and Eject always says what it did (here: it was not in the drive; it is set to stay out).
    /// </summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task Each_disc_says_where_it_is_and_eject_always_answers()
    {
        using var profile = new TestProfile("settings-eject");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Installed");
        Directory.CreateDirectory(dir);
        await VmQontrol.Core.Runtime.QemuImg.RunAsync(TestContext.Current.CancellationToken, "create", "-q", "-f", "qcow2", "-o", "preallocation=metadata", Path.Combine(dir, "disk0.qcow2"), "1G");
        File.WriteAllBytes(Path.Combine(dir, "win.iso"), [0]);
        File.WriteAllBytes(Path.Combine(dir, "unattended.iso"), [0]);
        MachineStore.Save(new MachineConfig
        {
            Name = "Installed",
            Guest = new GuestConfig { Os = "windows", Release = "10" },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
            Media = [new MediaConfig { Role = MediaRole.Installer, File = "win.iso", Connect = Connect.UntilInstalled },
                     new MediaConfig { Role = MediaRole.Unattended, File = "unattended.iso", Connect = Connect.UntilInstalled }],
        }, Path.Combine(dir, "vm.json"));

        var seen = await Ui.Run(() =>
        {
            var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
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
            var settings = shell.SettingsPage!;
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            Ui.Click(window, Named<ListBox>(view, "Sections").GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as SettingsSection)?.Key == "media"));
            Ui.PumpUntil(() => settings.Media.All(m => m.Status.Length > 0), "where each disc is");
            Ui.Pump(0.3);
            var drawn = view.GetLogicalDescendants().OfType<StackPanel>().Where(p => p.Name == "MediaStatus" && p.IsEffectivelyVisible)
                .Select(p => p.Children.OfType<TextBlock>().Single().Text).ToList();
            Ui.Capture(window, "s7", "settings-media-status-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Pump(0.3);
            Ui.Capture(window, "s7", "settings-media-status-light");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            var eject = view.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == "Eject" && b.IsEffectivelyVisible);
            Ui.Click(window, eject);
            Ui.PumpUntil(() => settings.SaveNote.Length > 0, "Eject to answer");
            var result = (drawn, Note: settings.SaveNote, First: settings.Media[0].Connect.Value);
            window.Close();
            return result;
        });

        Assert.Equal(2, seen.drawn.Count);
        Assert.All(seen.drawn, t => Assert.StartsWith("Out of the drive: the system is installed", t, StringComparison.Ordinal));
        Assert.Contains("was not in the drive", seen.Note, StringComparison.Ordinal);
        Assert.Equal(nameof(Connect.Never), seen.First);
    }

    /// <summary>Memory while it runs: shown for a running machine with a balloon, with what it holds now.</summary>
    [Trait("Tier", "Window")]
    [Fact]
    public async Task A_running_machine_shows_memory_it_may_use_now()
    {
        using var profile = new TestProfile("settings-live-memory");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Running");
        Directory.CreateDirectory(dir);
        await VmQontrol.Core.Runtime.QemuImg.RunAsync(TestContext.Current.CancellationToken, "create", "-q", "-f", "qcow2", Path.Combine(dir, "disk0.qcow2"), "1G");
        var m = new MachineConfig { Name = "Running", MemoryMiB = 512, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        MachineStore.Save(m, Path.Combine(dir, "vm.json"));
        var running = await VmQontrol.Core.Runtime.MachineRunner.StartAsync(m, dir, profile.Paths, VmQontrol.Core.Hosts.HostProbe.Probe(), new VmQontrol.Core.Runtime.StartOptions { Headless = true }, TestContext.Current.CancellationToken);
        try
        {
            var seen = await Ui.Run(() =>
            {
                var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
                var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                window.Show();
                _ = shell.StartAsync();
                Ui.PumpUntil(() => shell.Machines.Count == 1 && shell.Machines[0].IsActive, "the running machine", 20);
                Ui.Click(window, Named<ListBox>(window, "Library").GetVisualDescendants().OfType<ListBoxItem>().First());
                Ui.PumpUntil(() => shell.Selected is { Watcher: not null }, "the machine page, attached", 20);
                Ui.Click(window, Named<Button>(window.GetVisualDescendants().OfType<MachineView>().Single(), "OpenSettings"));
                Ui.PumpUntil(() => shell.SettingsPage is not null, "the settings", 15);
                var settings = shell.SettingsPage!;
                var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
                Ui.Click(window, Named<ListBox>(view, "Sections").GetVisualDescendants().OfType<ListBoxItem>().First(i => (i.DataContext as SettingsSection)?.Key == "memory"));
                Ui.PumpUntil(() => settings.LiveMemoryShown, "memory while it runs", 15);
                Ui.Pump(0.3);
                Ui.Capture(window, "s7", "settings-live-memory-dark");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Ui.Pump(0.3);
                Ui.Capture(window, "s7", "settings-live-memory-light");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                var result = (Drawn: Named<Border>(view, "LiveMemory").IsEffectivelyVisible, settings.LiveMemoryMaxGb, settings.LiveMemoryStatus);
                window.Close();
                return result;
            });
            Assert.True(seen.Drawn);
            Assert.Equal(0.5m, seen.LiveMemoryMaxGb);
            Assert.Contains("holds 512", seen.LiveMemoryStatus, StringComparison.Ordinal);
        }
        finally
        {
            await VmQontrol.Core.Runtime.MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }
    }
}
