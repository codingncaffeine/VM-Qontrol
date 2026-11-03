using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Phase 5 audit: Compact (Workstation's) gives back space, keeps every byte the system wrote, keeps links.</summary>
[Trait("Tier", "Integration")]
public sealed class DiskCompactTests
{
    /// <summary>A 64 MB disk with 1 MB of data, every cluster allocated (as a disk the system once filled and emptied).</summary>
    private static async Task<string> FilledDisk(string dir, string name, CancellationToken ct)
    {
        var raw = Path.Combine(dir, name + ".raw");
        var data = new byte[1024 * 1024];
        new Random(7).NextBytes(data);
        await using (var f = File.Create(raw))
        {
            f.Write(data);
            f.SetLength(64L * 1024 * 1024);
        }

        var disk = Path.Combine(dir, name + ".qcow2");
        await QemuImg.RunAsync(ct, "convert", "-q", "-f", "raw", "-O", "qcow2", "-o", "preallocation=full", raw, disk);
        File.Delete(raw);
        return disk;
    }

    private static async Task<long> Actual(string disk, CancellationToken ct) => (await QemuImg.InfoAsync(disk, ct))["actual-size"]!.GetValue<long>();

    [Fact]
    public async Task A_disk_gives_back_its_unused_space_and_keeps_its_data()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("compact");
        var disk = await FilledDisk(profile.Root, "disk0", ct);
        var copy = Path.Combine(profile.Root, "copy.qcow2");
        File.Copy(disk, copy);
        var before = await Actual(disk, ct);
        Assert.True(before > 60_000_000, $"the fixture is not filled ({before} bytes)");

        var r = await DiskCompact.CompactAsync(disk, ct);
        Assert.Equal(before, r.BytesBefore);
        Assert.True(r.BytesAfter < 4_000_000, $"compacted to {r.BytesAfter} bytes");
        Assert.Equal(await Actual(disk, ct), r.BytesAfter);
        await QemuImg.RunAsync(ct, "compare", "-q", copy, disk); // throws when any byte differs
        Assert.False(File.Exists(disk + ".compacting"));
    }

    [Fact]
    public async Task A_disk_with_snapshots_is_refused_and_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("compact-snap");
        var disk = await FilledDisk(profile.Root, "disk0", ct);
        await QemuImg.RunAsync(ct, "snapshot", "-c", "before", disk);
        var bytes = new FileInfo(disk).Length;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DiskCompact.CompactAsync(disk, ct));
        Assert.Contains("1 snapshot", ex.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, new FileInfo(disk).Length);
        Assert.Contains("before", (await QemuImg.RunAsync(ct, "snapshot", "-l", disk)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_linked_disk_stays_linked_to_the_same_base()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("compact-linked");
        var bases = Path.Combine(profile.Root, "bases");
        Directory.CreateDirectory(bases);
        var baseDisk = await FilledDisk(bases, "base", ct);
        var machine = Path.Combine(profile.Root, "machine");
        Directory.CreateDirectory(machine);
        var overlay = Path.Combine(machine, "disk0.qcow2");
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", "../bases/base.qcow2", "-F", "qcow2", overlay);
        var flat = Path.Combine(profile.Root, "flat.qcow2");
        await QemuImg.RunAsync(ct, "convert", "-q", "-O", "qcow2", overlay, flat);

        await DiskCompact.CompactAsync(overlay, ct);
        var info = await QemuImg.InfoAsync(overlay, ct);
        Assert.Equal("../bases/base.qcow2", info["backing-filename"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(baseDisk), DiskState.BackingFile(overlay));
        await QemuImg.RunAsync(ct, "compare", "-q", flat, overlay);
        // The control: the overlay holds none of the base's data (convert without -B would have copied the megabyte in).
        Assert.True(await Actual(overlay, ct) < 1_000_000, "the compacted overlay copied its base in");
    }
}
