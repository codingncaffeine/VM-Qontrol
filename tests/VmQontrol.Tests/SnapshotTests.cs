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
