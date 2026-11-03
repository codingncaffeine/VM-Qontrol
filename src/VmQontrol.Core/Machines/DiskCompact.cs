using System.Text.Json.Nodes;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Machines;

/// <summary>What compacting a disk did: the file's space on this computer before and after.</summary>
public sealed record CompactResult(long BytesBefore, long BytesAfter)
{
    public long Saved => Math.Max(0, BytesBefore - BytesAfter);
}

/// <summary>
/// Workstation's Compact: the disk is rewritten with only the data the system still uses (qemu-img convert), so space it
/// freed comes back to this computer. The machine must be off. A disk with snapshots is refused (a rewrite keeps no
/// snapshots, as Workstation also refuses); a linked disk stays linked to the same base.
/// </summary>
public static class DiskCompact
{
    /// <summary>Why this disk cannot be compacted, in words, or <c>null</c> when it can.</summary>
    public static string? Refusal(JsonObject info)
    {
        if (info["format"]?.GetValue<string>() is not "qcow2") return "Only qcow2 disks can be compacted.";
        if (info["snapshots"] is JsonArray { Count: > 0 } snaps)
            return $"This disk has {snaps.Count} snapshot{(snaps.Count == 1 ? "" : "s")}; compacting would lose {(snaps.Count == 1 ? "it" : "them")}. Delete the snapshots first.";
        return null;
    }

    public static async Task<CompactResult> CompactAsync(string disk, CancellationToken ct = default)
    {
        var info = await QemuImg.InfoAsync(disk, ct).ConfigureAwait(false);
        if (Refusal(info) is { } why) throw new InvalidOperationException(why);
        var before = info["actual-size"]?.GetValue<long>() ?? new FileInfo(disk).Length;
        var free = new DriveInfo(Path.GetDirectoryName(Path.GetFullPath(disk))!).AvailableFreeSpace;
        if (free < before) throw new IOException($"Compacting needs room for a second copy while it works ({Gb(before)}); this drive has {Gb(free)} free.");

        var temp = disk + ".compacting";
        if (File.Exists(temp)) File.Delete(temp);
        var args = new List<string> { "convert", "-q", "-f", "qcow2", "-O", "qcow2", "-o", "lazy_refcounts=on,nocow=on" };
        if (info["backing-filename"]?.GetValue<string>() is { } backing)
        {
            // Stays an overlay on the same base, written exactly as it is in the original (relative stays relative).
            args.AddRange(["-B", backing, "-F", info["backing-filename-format"]?.GetValue<string>() ?? "qcow2"]);
        }

        args.AddRange([disk, temp]);
        try
        {
            await QemuImg.RunAsync(ct, [.. args]).ConfigureAwait(false);
            await QemuImg.RunAsync(ct, "check", "-q", temp).ConfigureAwait(false);
            var made = await QemuImg.InfoAsync(temp, ct).ConfigureAwait(false);
            if (made["virtual-size"]?.GetValue<long>() != info["virtual-size"]?.GetValue<long>())
                throw new IOException("The compacted copy came out a different size; the original is kept.");
            File.Move(temp, disk, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }

        // actual-size = blocks allocated on this computer, so holes the system already punched do not count.
        var after = (await QemuImg.InfoAsync(disk, ct).ConfigureAwait(false))["actual-size"]?.GetValue<long>() ?? new FileInfo(disk).Length;
        return new CompactResult(before, after);
    }

    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
}
