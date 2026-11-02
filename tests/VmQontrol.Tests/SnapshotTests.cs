using System.Text.Json.Nodes;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;
using VmQontrol.Core.Snapshots;

namespace VmQontrol.Tests;

[Trait("Tier", "Integration")]
public sealed class SnapshotTests
{
    /// <summary>The control: qemu-img's own list of the snapshots inside the disk.</summary>
    private static async Task<List<(string Tag, long VmStateSize)>> InDiskAsync(string disk, CancellationToken ct)
    {
        var info = await QemuImg.InfoAsync(disk, ct);
        return (info["snapshots"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => (s["name"]!.GetValue<string>(), s["vm-state-size"]?.GetValue<long>() ?? 0)).ToList();
    }

    [Fact]
    public void AutoProtect_is_due_after_its_interval_of_running_and_prunes_only_its_own_oldest()
    {
        var start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var on = new AutoProtectConfig { Enabled = true, IntervalMinutes = 60, Keep = 2 };
        Snapshot S(string tag, int minutes, bool auto) => new() { Tag = tag, Name = tag, Taken = start.AddMinutes(minutes), Auto = auto };
        var empty = new SnapshotList();
        Assert.False(AutoProtect.IsDue(on, empty, start, start.AddMinutes(59)));
        Assert.True(AutoProtect.IsDue(on, empty, start, start.AddMinutes(60)));
        Assert.False(AutoProtect.IsDue(on with { Enabled = false }, empty, start, start.AddDays(1)));
        var list = new SnapshotList { Snapshots = [S("mine", 10, false), S("a1", 60, true), S("a2", 120, true), S("a3", 180, true)] };
        Assert.False(AutoProtect.IsDue(on, list, start, start.AddMinutes(239)));
        Assert.True(AutoProtect.IsDue(on, list, start, start.AddMinutes(240)));
        Assert.Equal(["a1"], AutoProtect.ToPrune(on, list).Select(x => x.Tag));
        Assert.Empty(AutoProtect.ToPrune(on with { Keep = 5 }, list));
    }

    [Fact]
    public async Task AutoProtect_on_a_running_machine_keeps_the_newest_automatic_snapshots_and_the_persons_own()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("autoprotect");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "ap");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig
        {
            Name = "ap", MemoryMiB = 128, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
        };
        var disk = Path.Combine(dir, "disk0.qcow2");
        await QemuImg.CreateAsync(disk, "qcow2", 1, ct: ct);
        var store = new SnapshotStore(m, dir, Path.Combine(profile.Root, "meta"));
        var config = new AutoProtectConfig { Enabled = true, IntervalMinutes = 30, Keep = 2 };
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            await using var watcher = await MachineWatcher.AttachAsync(running, ct);
            var mine = await store.TakeAsync("Mine", "", watcher, ct);
            var since = DateTimeOffset.Now;
            Assert.Null(await AutoProtect.RunIfDueAsync(store, config, watcher, since, since.AddMinutes(29), ct));
            for (var i = 1; i <= 4; i++) Assert.NotNull(await AutoProtect.RunIfDueAsync(store, config, watcher, since, since.AddMinutes(30 * i), ct));
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        var list = store.Load();
        Assert.Equal(["Mine"], list.Snapshots.Where(x => !x.Auto).Select(x => x.Name));
        Assert.Equal(2, list.Snapshots.Count(x => x.Auto));
        // The control: the disk itself holds exactly the three listed snapshots.
        Assert.Equal(list.Snapshots.Select(x => x.Tag).Order(), (await InDiskAsync(disk, ct)).Select(x => x.Tag).Order());
    }

    [Fact]
    public async Task A_suspended_machine_refuses_to_go_back_to_a_snapshot_and_its_disk_is_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("snap-suspended");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "snap");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "snap", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        var disk = Path.Combine(dir, "disk0.qcow2");
        await QemuImg.CreateAsync(disk, "qcow2", 1, ct: ct);
        var store = new SnapshotStore(m, dir, Path.Combine(profile.Root, "meta"));
        var snap = await store.TakeAsync("Before", "", null, ct);
        File.WriteAllText(MachineSuspend.StatePath(dir), "saved state");
        var before = await QemuImg.RunAsync(ct, "info", "--output=json", disk);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreAsync(snap, null, ct));

        Assert.Contains("suspended", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await QemuImg.RunAsync(ct, "info", "--output=json", disk));
        MachineSuspend.Discard(dir);
        await store.RestoreAsync(snap, null, ct);
    }

    [Fact]
    public async Task Snapshots_are_taken_running_and_off_restored_and_deleted_with_the_tree_kept_whole()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = HostProbe.Probe();
        Assert.SkipWhen(FirmwareLocator.Find(host.FirmwareFiles, false) is null, "no OVMF firmware on this host");
        using var profile = new TestProfile("snap");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "snap");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "snap", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        var disk = Path.Combine(dir, "disk0.qcow2");
        await QemuImg.CreateAsync(disk, "qcow2", 1, ct: ct);
        var store = new SnapshotStore(m, dir, Path.Combine(profile.Root, "meta"));

        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, host, new StartOptions { Headless = true }, ct);
        Snapshot first;
        try
        {
            await using var watcher = await MachineWatcher.AttachAsync(running, ct);
            first = await store.TakeAsync("Fresh", "Before anything", watcher, ct);
            Assert.True(first.WithMemory);
            Assert.True(File.Exists(Path.Combine(store.ScreenshotDirectory, first.Screenshot!)));
            Assert.Contains(await InDiskAsync(disk, ct), s => s.Tag == first.Tag && s.VmStateSize > 0);
            await store.RestoreAsync(first, watcher, ct);
            Assert.Equal("running", await MachineRunner.StatusAsync(running, ct));
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        var second = await store.TakeAsync("Off", "", null, ct);
        Assert.False(second.WithMemory);
        Assert.Equal(first.Tag, second.Parent);
        Assert.Equal(second.Tag, store.Load().Current);
        Assert.Contains(await InDiskAsync(disk, ct), s => s.Tag == second.Tag && s.VmStateSize == 0);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            // A snapshot without memory cannot be restored into a running machine; the reason says so.
            var r = await MachineRunner.StartAsync(m, dir, profile.Paths, host, new StartOptions { Headless = true }, ct);
            try
            {
                await using var w = await MachineWatcher.AttachAsync(r, ct);
                await store.RestoreAsync(second, w, ct);
            }
            finally
            {
                await MachineRunner.PowerOffAsync(r, CancellationToken.None);
                await MachineRunner.WaitForExitAsync(r, TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        });

        await store.RestoreAsync(first, null, ct);
        Assert.Equal(first.Tag, store.Load().Current);
        await store.DeleteAsync(first, null, ct);
        var left = store.Load();
        Assert.Equal([second.Tag], left.Snapshots.Select(s => s.Tag));
        Assert.Null(left.Snapshots[0].Parent);
        Assert.DoesNotContain(await InDiskAsync(disk, ct), s => s.Tag == first.Tag);
    }
}
