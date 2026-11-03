using VmQontrol.Core.Library;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Phase 19 item 7 (s11): every disk and disc of the library, its sizes and users, and what nothing uses.</summary>
public sealed class MediaInventoryTests
{
    [Fact]
    public async Task The_library_disks_discs_bases_unused_and_missing_files_are_listed_and_internal_files_are_not()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("media");
        var lib = profile.Paths.DefaultLibrary;
        var bases = Path.Combine(lib, MachineLifecycle.BasesFolder);
        Directory.CreateDirectory(bases);
        var baseDisk = Path.Combine(bases, "golden.qcow2");
        await QemuImg.CreateAsync(baseDisk, "qcow2", 2, ct: ct);

        // Clone: a linked disk on the base, an installer disc, a firmware variables file, a discard-session overlay.
        var clone = Path.Combine(lib, "Clone");
        Directory.CreateDirectory(Path.Combine(clone, DiscardSession.FolderName));
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-F", "qcow2", "-b", baseDisk, Path.Combine(clone, "disk0.qcow2"));
        IsoWriter.Write(Path.Combine(lib, "installer.iso"), "INSTALL", new Dictionary<string, byte[]> { ["A.TXT"] = "a"u8.ToArray() });
        File.WriteAllBytes(Path.Combine(clone, "OVMF_VARS.fd"), new byte[4096]);
        await QemuImg.CreateAsync(Path.Combine(clone, DiscardSession.FolderName, "disk0.qcow2"), "qcow2", 2, ct: ct);
        MachineStore.Save(new MachineConfig
        {
            Name = "Clone",
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
            Media = [new MediaConfig { Role = MediaRole.Installer, File = Path.Combine(lib, "installer.iso") }],
        }, Path.Combine(clone, "vm.json"));

        // Gone: its disk was deleted by hand.
        var gone = Path.Combine(lib, "Gone");
        Directory.CreateDirectory(gone);
        MachineStore.Save(new MachineConfig { Name = "Gone", Disks = [new DiskConfig { File = "disk0.qcow2" }] }, Path.Combine(gone, "vm.json"));

        // Left behind: a disk and a disc nothing uses.
        await QemuImg.CreateAsync(Path.Combine(lib, "old-copy.qcow2"), "qcow2", 3, ct: ct);
        IsoWriter.Write(Path.Combine(lib, "fedora-old.iso"), "OLD", new Dictionary<string, byte[]> { ["B.TXT"] = "b"u8.ToArray() });

        var items = await MediaInventory.ScanAsync(LibraryScanner.Scan(lib), [lib], [], ct);
        string Line(MediaItem i) => $"{Path.GetFileName(i.Path)}|{i.Kind}|{i.Status}|{string.Join("+", i.UsedBy)}";
        var lines = items.Select(Line).Order().ToList();

        Assert.Equal([
            "disk0.qcow2|Disk|InUse|Clone",
            "disk0.qcow2|Disk|Missing|Gone",
            "fedora-old.iso|Disc|Unused|",
            "golden.qcow2|Base|InUse|Clone (linked clone)",
            "installer.iso|Disc|InUse|Clone",
            "old-copy.qcow2|Disk|Unused|",
        ], lines); // control: the session overlay and OVMF_VARS.fd are not listed
        var unused = items.Single(i => i.Path.EndsWith("old-copy.qcow2", StringComparison.Ordinal));
        Assert.Equal(3L << 30, unused.VirtualBytes);
        Assert.Equal("qcow2", unused.Format);
        Assert.True(unused.ActualBytes is > 0 and < 10 << 20); // a fresh qcow2 holds almost nothing on the drive
        Assert.EndsWith("golden.qcow2", items.Single(i => i.Path == Path.Combine(clone, "disk0.qcow2")).Backing);
    }
}

/// <summary>The Disks and discs window, walked from the welcome page.</summary>
public sealed class MediaWindowTests
{
    private static T Named<T>(Avalonia.Controls.Control root, string name) where T : Avalonia.Controls.Control =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>().First(c => c.Name == name && c.IsEffectivelyVisible);

    [Trait("Tier", "Window")]
    [Fact]
    public async Task Disks_and_discs_lists_the_library_and_an_unused_disk_is_deleted_after_asking()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("media-window");
        var lib = profile.Paths.DefaultLibrary;
        var web = Path.Combine(lib, "Web");
        Directory.CreateDirectory(web);
        await QemuImg.CreateAsync(Path.Combine(web, "disk0.qcow2"), "qcow2", 20, ct: ct);
        MachineStore.Save(new MachineConfig { Name = "Web", Disks = [new DiskConfig { File = "disk0.qcow2" }] }, Path.Combine(web, "vm.json"));
        var leftover = Path.Combine(lib, "old-copy.qcow2");
        await QemuImg.CreateAsync(leftover, "qcow2", 8, ct: ct);

        var seen = await Ui.Run(() =>
        {
            var shell = new VmQontrol.App.ViewModels.ShellViewModel(profile.Paths, VmQontrol.Core.AppSettings.Load(profile.Paths)) { WatchUsb = false };
            var window = new VmQontrol.App.Views.MainWindow { DataContext = shell, Width = 1320, Height = 900 };
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            window.Show();
            _ = shell.StartAsync();
            Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
            Ui.Click(window, Named<Avalonia.Controls.Button>(window, "OpenMedia"));
            Ui.PumpUntil(() => shell.Media is { Loading: false }, "the list", 20);
            Ui.Pump(0.3);
            var all = shell.Media!.Shown.Select(r => $"{r.Name}|{r.StatusText}|{r.UsedByText}").ToList();
            var summary = shell.Media.Summary;
            Ui.Capture(window, "s11", "media-dark");
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Ui.Pump(0.2);
            Ui.Capture(window, "s11", "media-light");
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

            shell.Media.Filter = VmQontrol.App.ViewModels.MediaViewModel.FilterOptions.First(o => o.Value == "unused");
            Ui.Pump(0.2);
            var unusedOnly = shell.Media.Shown.Select(r => r.Name).ToList();
            var delete = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Named<Avalonia.Controls.ItemsControl>(window, "MediaList"))
                .OfType<Avalonia.Controls.Button>().First(b => (b.Content as string) == "Delete…" && b.IsEffectivelyVisible);
            Ui.Click(window, delete);
            Ui.PumpUntil(() => shell.Notice is not null, "the confirmation");
            var asked = shell.Notice!.Title;
            var stillThere = File.Exists(leftover);
            Ui.Click(window, Named<Avalonia.Controls.Button>(window, "NoticeConfirm"));
            Ui.PumpUntil(() => !File.Exists(leftover) && shell.Media is { Loading: false, Empty: true }, "the disk deleted and the list empty", 20);
            Ui.Click(window, Named<Avalonia.Controls.Button>(window, "MediaClose"));
            Ui.PumpUntil(() => shell.Media is null, "the window closed");
            window.Close();
            return (all, summary, unusedOnly, asked, stillThere);
        });

        Assert.Equal(["disk0.qcow2|In use|Used by Web", "old-copy.qcow2|Unused|No machine uses it"], seen.all);
        Assert.Contains("1 unused", seen.summary, StringComparison.Ordinal);
        Assert.Equal(["old-copy.qcow2"], seen.unusedOnly);
        Assert.Equal("Delete old-copy.qcow2?", seen.asked);
        Assert.True(seen.stillThere); // nothing deleted before the answer
        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(Path.Combine(web, "disk0.qcow2"))); // control: the machine's disk stays
    }
}
