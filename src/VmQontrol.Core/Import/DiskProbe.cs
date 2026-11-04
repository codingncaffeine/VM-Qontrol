using System.Diagnostics;
using System.Globalization;

namespace VmQontrol.Core.Import;

/// <summary>What a disk's partition table says about the machine it came from.</summary>
/// <param name="Table">"gpt", "mbr" or null (no table: an empty or unpartitioned disk).</param>
/// <param name="Uefi">It starts with UEFI (an EFI system partition), else BIOS; null when the table cannot tell.</param>
/// <param name="Os">"windows", "linux", "macos", "bsd" or null.</param>
/// <param name="Evidence">The partitions that decided it, in words.</param>
public sealed record DiskFacts(string? Table, bool? Uefi, string? Os, string Evidence);

/// <summary>
/// Recognizes a disk that comes with no settings (a bare .vmdk, a Hyper-V .vhdx, a qcow2) from its partition table,
/// the way an installer or a firmware would: QEMU's own qemu-io reads the first 34 KiB in any image format, read-only;
/// the MBR and GPT layouts and partition type GUIDs are the UEFI specification's and each system's published ones.
/// An EFI system partition = UEFI; a BIOS boot partition or a plain MBR = BIOS. Windows has its reserved (MSR) or
/// recovery partition or NTFS, Linux its own types, macOS APFS or HFS+, the BSDs theirs.
/// </summary>
public static class DiskProbe
{
    private const string Esp = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";
    private const string BiosBoot = "21686148-6449-6e6f-744e-656564454649";
    private const string MicrosoftData = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7";

    private static readonly Dictionary<string, (string Os, string Name)> GptTypes = new()
    {
        ["e3c9e316-0b5c-4db8-817d-f92df00215ae"] = ("windows", "a Microsoft reserved partition"),
        ["de94bba4-06d1-4d40-a16a-bfd50179d6ac"] = ("windows", "a Windows recovery partition"),
        ["0fc63daf-8483-4772-8e79-3d69d8477de4"] = ("linux", "a Linux file system"),
        ["4f68bce3-e8cd-4db1-96e7-fbcaf984b709"] = ("linux", "a Linux root partition"),
        ["e6d6d379-f507-44c2-a23c-238f2a3df928"] = ("linux", "Linux LVM"),
        ["a19d880f-05fc-4d3b-a006-743f0f84911e"] = ("linux", "Linux RAID"),
        ["0657fd6d-a4ab-43c4-84e5-0933c84b4f4f"] = ("linux", "Linux swap"),
        ["933ac7e1-2eb4-4f13-b844-0e14e2aef915"] = ("linux", "a Linux home partition"),
        ["bc13c2ff-59e6-4262-a352-b275fd6f7172"] = ("linux", "a Linux boot partition"),
        ["ca7d7ccb-63ed-4c53-861c-1742536059cc"] = ("linux", "LUKS encryption"),
        ["7c3457ef-0000-11aa-aa11-00306543ecac"] = ("macos", "APFS"),
        ["48465300-0000-11aa-aa11-00306543ecac"] = ("macos", "HFS+"),
        ["516e7cb4-6ecf-11d6-8ff8-00022d09712b"] = ("bsd", "a FreeBSD partition"),
        ["516e7cb6-6ecf-11d6-8ff8-00022d09712b"] = ("bsd", "a FreeBSD file system"),
        ["516e7cba-6ecf-11d6-8ff8-00022d09712b"] = ("bsd", "FreeBSD ZFS"),
        ["83bd6b9d-7f41-11dc-be0b-001560b84f0f"] = ("bsd", "a FreeBSD boot partition"),
        ["824cc7a0-36a8-11e3-890a-952519ad3f61"] = ("bsd", "an OpenBSD partition"),
    };

    private static readonly Dictionary<byte, (string Os, string Name)> MbrTypes = new()
    {
        [0x07] = ("windows", "NTFS"),
        [0x27] = ("windows", "a Windows recovery partition"),
        [0x83] = ("linux", "a Linux file system"),
        [0x8e] = ("linux", "Linux LVM"),
        [0x82] = ("linux", "Linux swap"),
        [0xfd] = ("linux", "Linux RAID"),
        [0xa5] = ("bsd", "a FreeBSD slice"),
        [0xa6] = ("bsd", "an OpenBSD slice"),
        [0xa9] = ("bsd", "a NetBSD slice"),
        [0xaf] = ("macos", "HFS+"),
    };

    /// <summary>QEMU's name for a disk's format, from its extension (what qemu-io and qemu-img take with -f).</summary>
    public static string Format(string disk) => Path.GetExtension(disk).ToLowerInvariant() switch
    {
        ".vmdk" => "vmdk",
        ".vdi" => "vdi",
        ".vhd" => "vpc",
        ".vhdx" => "vhdx",
        ".qcow2" => "qcow2",
        ".qcow" => "qcow",
        ".qed" => "qed",
        ".hdd" => "parallels",
        _ => "raw",
    };

    /// <summary>Reads the disk's first sectors and decides; null when they could not be read.</summary>
    public static DiskFacts? Probe(string disk)
    {
        var bytes = ReadStart(disk, 34 * 1024);
        return bytes is null ? null : Decide(bytes);
    }

    /// <summary>The first <paramref name="count"/> bytes through qemu-io (read-only, any format); null when it fails.</summary>
    public static byte[]? ReadStart(string disk, int count)
    {
        try
        {
            var start = new ProcessStartInfo("qemu-io") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-r", "-f", Format(disk), "-c", $"read -v 0 {count.ToString(CultureInfo.InvariantCulture)}", disk }) start.ArgumentList.Add(a);
            using var p = Process.Start(start);
            if (p is null) return null;
            var stderr = p.StandardError.ReadToEndAsync();
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30_000)) { p.Kill(); return null; }
            _ = stderr.Result;
            return p.ExitCode == 0 ? ParseDump(text, count) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>qemu-io's "read -v" dump: "00000000:  eb 63 90 …  ascii" lines; repeated lines are never folded.</summary>
    public static byte[]? ParseDump(string text, int count)
    {
        var bytes = new byte[count];
        var seen = 0;
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon != 8 || !int.TryParse(line.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset)) continue;
            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < 16 && i < fields.Length && offset + i < count; i++)
            {
                if (fields[i].Length != 2 || !byte.TryParse(fields[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) break;
                bytes[offset + i] = b;
                seen = Math.Max(seen, offset + i + 1);
            }
        }

        return seen >= 1024 ? bytes : null;
    }

    /// <summary>The decision from the first sectors (512-byte sectors; GPT header at LBA 1).</summary>
    public static DiskFacts Decide(byte[] start)
    {
        if (start.Length < 1024 || start[510] != 0x55 || start[511] != 0xAA)
            return new(null, null, null, "it has no partition table (an empty disk, or one used whole)");
        var types = Enumerable.Range(0, 4).Select(i => start[446 + 16 * i + 4]).Where(t => t != 0).ToList();
        if (types.Contains((byte)0xEE) && System.Text.Encoding.ASCII.GetString(start, 512, 8) == "EFI PART")
            return Gpt(start);

        var found = types.Where(MbrTypes.ContainsKey).Select(t => MbrTypes[t]).ToList();
        var uefi = types.Contains((byte)0xEF);
        var os = Pick(found.Select(f => f.Os));
        var what = found.Count == 0 ? "partitions of no known system" : string.Join(", ", found.Select(f => f.Name).Distinct());
        return new("mbr", uefi, os, $"an MBR partition table with {what}{(uefi ? " and an EFI system partition" : "")}");
    }

    private static DiskFacts Gpt(byte[] start)
    {
        var entriesAt = (long)BitConverter.ToUInt64(start, 512 + 72) * 512;
        var count = (int)Math.Min(BitConverter.ToUInt32(start, 512 + 80), 128);
        var size = (int)BitConverter.ToUInt32(start, 512 + 84);
        if (size < 128) size = 128;
        var guids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var at = entriesAt + (long)i * size;
            if (at + 16 > start.Length) break;
            var guid = new Guid(start.AsSpan((int)at, 16)).ToString();
            if (guid != Guid.Empty.ToString()) guids.Add(guid);
        }

        var esp = guids.Contains(Esp);
        var biosBoot = guids.Contains(BiosBoot);
        var found = guids.Where(GptTypes.ContainsKey).Select(g => GptTypes[g]).ToList();
        // Microsoft basic data is Windows' own, but Linux disks use it for shared data too: Windows only when nothing else says otherwise.
        var os = Pick(found.Select(f => f.Os)) ?? (guids.Contains(MicrosoftData) ? "windows" : null);
        var names = found.Select(f => f.Name).Concat(guids.Contains(MicrosoftData) && found.Count == 0 ? ["a Microsoft data partition"] : []).Distinct().ToList();
        if (esp) names.Insert(0, "an EFI system partition");
        if (biosBoot) names.Insert(0, "a BIOS boot partition");
        bool? uefi = esp ? true : biosBoot ? false : null;
        return new("gpt", uefi, os, $"a GPT partition table with {(names.Count == 0 ? "no partitions of a known system" : string.Join(", ", names))}");
    }

    /// <summary>The system most partitions point to (Windows reserved/recovery and Linux types never appear together by chance).</summary>
    private static string? Pick(IEnumerable<string> votes) =>
        votes.GroupBy(v => v).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
}
