using System.Globalization;

namespace VmQontrol.Core.Devices;

public enum HostDriveKind
{
    /// <summary>A whole disk: an SSD, a hard disk, a USB stick, a memory card.</summary>
    Disk,

    /// <summary>One partition of a disk.</summary>
    Partition,

    /// <summary>A CD, DVD or Blu-ray drive.</summary>
    Optical,
}

/// <summary>
/// A drive of this computer as the machine pages show it. <see cref="StablePath"/> names it the same way after a reboot
/// or a replug (a /dev/disk/by-id link), which is what a machine's settings keep; <see cref="DevicePath"/> is what it is
/// called right now.
/// </summary>
public sealed record HostDrive
{
    public required string Name { get; init; }

    public required string DevicePath { get; init; }

    public required string StablePath { get; init; }

    public required HostDriveKind Kind { get; init; }

    /// <summary>The maker's name for it ("Samsung SSD 990 PRO 4TB"), or the device name when it gives none.</summary>
    public required string Model { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>usb, ata, nvme, scsi... as udev reports it.</summary>
    public string? Transport { get; init; }

    public bool Removable { get; init; }

    /// <summary>The drive itself refuses writes (a locked memory card, a disc).</summary>
    public bool ReadOnly { get; init; }

    public string? Label { get; init; }

    public string? FileSystem { get; init; }

    /// <summary>Where this drive or any partition on it is mounted, and "swap" when it holds swap.</summary>
    public IReadOnlyList<string> Mounts { get; init; } = [];

    /// <summary>It, or a partition on it, holds this computer's own system (/, /boot, /efi, /home, /usr, /var or swap).</summary>
    public bool HoldsSystem { get; init; }

    /// <summary>Something on this computer builds on it (an encrypted volume, LVM, RAID).</summary>
    public bool HasHolders { get; init; }

    /// <summary>The disk this partition is on (<c>null</c> for a whole disk or drive).</summary>
    public string? Parent { get; init; }

    /// <summary>An optical drive with no disc, or a card reader with no card.</summary>
    public bool Empty => SizeBytes == 0;

    /// <summary>Whether the computer is using it now: a guest writing to it would corrupt what the host has open.</summary>
    public bool InUse => Mounts.Count > 0 || HasHolders;

    /// <summary>Why the machine may not write to it, or <c>null</c> when it may (after the person confirms).</summary>
    public string? WriteRefusal =>
        HoldsSystem ? "it holds this computer's own system"
        : Kind == HostDriveKind.Optical ? "a disc drive is read-only"
        : ReadOnly ? "the drive itself is write-protected"
        : Mounts.Count > 0 ? $"it is in use on this computer ({string.Join(", ", Mounts)}); unmount it first"
        : HasHolders ? "it is in use on this computer (an encrypted volume, LVM or RAID builds on it)"
        : null;

    public string Describe()
    {
        var what = Kind switch { HostDriveKind.Optical => "disc drive", HostDriveKind.Partition => "partition", _ => Removable ? "removable disk" : "disk" };
        var size = Empty ? (Kind == HostDriveKind.Optical ? "no disc" : "no media") : FormatSize(SizeBytes);
        return $"{Model} ({what}, {size})";
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 40 ? (bytes / (double)(1L << 40)).ToString("0.#", CultureInfo.InvariantCulture) + " TiB"
        : bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GiB"
        : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MiB";
}

/// <summary>
/// Lists this computer's drives from sysfs, the udev database and the mount table, the way lsblk does, without running
/// anything. Loop devices, RAM disks, zram and device-mapper volumes are left out: they are files or views of other drives.
/// </summary>
public static class HostDrives
{
    private static readonly string[] SystemMounts = ["/", "/boot", "/efi", "/boot/efi", "/home", "/usr", "/var"];

    public static IReadOnlyList<HostDrive> List(bool includeLoop = false, string sys = "/sys", string dev = "/dev", string proc = "/proc", string udev = "/run/udev/data")
    {
        var mounts = ReadMounts(proc);
        var swaps = ReadSwaps(proc);
        var stable = StableNames(Path.Combine(dev, "disk", "by-id"));
        var drives = new List<HostDrive>();
        var root = Path.Combine(sys, "block");
        if (!Directory.Exists(root)) return drives;
        foreach (var blockDir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(blockDir);
            var loop = name.StartsWith("loop", StringComparison.Ordinal);
            if (loop && (!includeLoop || !File.Exists(Path.Combine(blockDir, "loop", "backing_file")))) continue;
            if (name.StartsWith("ram", StringComparison.Ordinal) || name.StartsWith("zram", StringComparison.Ordinal)
                || name.StartsWith("dm-", StringComparison.Ordinal) || name.StartsWith("md", StringComparison.Ordinal)
                || name.StartsWith("nbd", StringComparison.Ordinal) || name.StartsWith("fd", StringComparison.Ordinal)) continue;

            var optical = name.StartsWith("sr", StringComparison.Ordinal) || Read(Path.Combine(blockDir, "device", "type")) == "5";
            var partitions = Directory.EnumerateDirectories(blockDir).Where(d => File.Exists(Path.Combine(d, "partition"))).Order(StringComparer.Ordinal).ToList();
            var disk = Describe(blockDir, name, optical ? HostDriveKind.Optical : HostDriveKind.Disk, null, dev, udev, stable, mounts, swaps);
            var parts = partitions.Select(p => Describe(p, Path.GetFileName(p), HostDriveKind.Partition, name, dev, udev, stable, mounts, swaps))
                .Select(p => p with { Model = p.Label is { Length: > 0 } label ? $"{label} on {disk.Model}" : $"Partition {p.Name} on {disk.Model}" }).ToList();
            // A disk is in use, and holds the system, when any partition on it does.
            disk = disk with
            {
                Mounts = [.. disk.Mounts, .. parts.SelectMany(p => p.Mounts)],
                HoldsSystem = disk.HoldsSystem || parts.Any(p => p.HoldsSystem),
                HasHolders = disk.HasHolders || parts.Any(p => p.HasHolders),
            };
            drives.Add(disk);
            drives.AddRange(parts);
        }

        return drives;
    }

    /// <summary>The drive a saved path names now (a by-id link or a device path), or <c>null</c> when it is not connected.</summary>
    public static HostDrive? Find(string path, IReadOnlyList<HostDrive>? drives = null)
    {
        drives ??= List(includeLoop: path.StartsWith("/dev/loop", StringComparison.Ordinal));
        string resolved;
        try
        {
            resolved = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return null;
        }

        return drives.FirstOrDefault(d => d.StablePath == path || d.DevicePath == path || d.DevicePath == resolved);
    }

    private static HostDrive Describe(string dir, string name, HostDriveKind kind, string? parent, string dev, string udev,
        Dictionary<string, string> stable, Dictionary<string, List<string>> mounts, HashSet<string> swaps)
    {
        var numbers = Read(Path.Combine(dir, "dev")) ?? "";
        var props = ReadUdev(Path.Combine(udev, "b" + numbers));
        var sectors = long.TryParse(Read(Path.Combine(dir, "size")), CultureInfo.InvariantCulture, out var s) ? s : 0;
        var diskDir = parent is null ? dir : Path.GetDirectoryName(dir)!;
        var model = props.GetValueOrDefault("ID_MODEL")?.Replace('_', ' ') ?? Read(Path.Combine(diskDir, "device", "model"));
        if (name.StartsWith("loop", StringComparison.Ordinal)) model = Path.GetFileName(Read(Path.Combine(dir, "loop", "backing_file")) ?? name);
        var vendor = props.GetValueOrDefault("ID_VENDOR")?.Replace('_', ' ');
        if (model is null or "") model = name;
        else if (vendor is { Length: > 0 } && !model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) && vendor != "ATA") model = $"{vendor} {model}";

        List<string> here = [.. (mounts.GetValueOrDefault(numbers) ?? []).Union(mounts.GetValueOrDefault("name:" + name) ?? [])];
        var devicePath = Path.Combine(dev, name);
        var swap = swaps.Contains(devicePath);
        var allMounts = swap ? [.. here, "swap"] : here;
        return new HostDrive
        {
            Name = name,
            DevicePath = devicePath,
            StablePath = stable.GetValueOrDefault(name) ?? devicePath,
            Kind = kind,
            Model = model.Trim(),
            SizeBytes = sectors * 512,
            Transport = props.GetValueOrDefault("ID_BUS"),
            Removable = Read(Path.Combine(diskDir, "removable")) == "1",
            ReadOnly = Read(Path.Combine(dir, "ro")) == "1",
            Label = props.GetValueOrDefault("ID_FS_LABEL"),
            FileSystem = props.GetValueOrDefault("ID_FS_TYPE"),
            Mounts = allMounts,
            HoldsSystem = swap || here.Any(m => SystemMounts.Contains(m)),
            HasHolders = Directory.Exists(Path.Combine(dir, "holders")) && Directory.EnumerateFileSystemEntries(Path.Combine(dir, "holders")).Any(),
            Parent = parent,
        };
    }

    /// <summary>Device name → its most readable /dev/disk/by-id link (model and serial rather than a WWN or EUI number).</summary>
    private static Dictionary<string, string> StableNames(string byId)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(byId)) return map;
        static int Rank(string link)
        {
            var n = Path.GetFileName(link);
            return n.StartsWith("wwn-", StringComparison.Ordinal) || n.StartsWith("nvme-eui.", StringComparison.Ordinal) || n.StartsWith("nvme-nvme.", StringComparison.Ordinal) ? 1 : 0;
        }

        foreach (var link in Directory.EnumerateFileSystemEntries(byId).OrderBy(Rank).ThenBy(l => l.Length).ThenBy(l => l, StringComparer.Ordinal))
        {
            try
            {
                if (File.ResolveLinkTarget(link, returnFinalTarget: true) is { } target) map.TryAdd(Path.GetFileName(target.FullName), link);
            }
            catch (IOException)
            {
            }
        }

        return map;
    }

    /// <summary>
    /// Mount points by "major:minor" AND by source device name: btrfs (and other multi-device file systems) report an
    /// anonymous device number in the mount table, so only the source ("/dev/nvme0n1p2") names the drive under them.
    /// </summary>
    private static Dictionary<string, List<string>> ReadMounts(string proc)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string point)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            if (!list.Contains(point)) list.Add(point);
        }

        foreach (var line in ReadLines(Path.Combine(proc, "self", "mountinfo")))
        {
            var f = line.Split(' ');
            if (f.Length < 5) continue;
            var point = f[4].Replace("\\040", " ", StringComparison.Ordinal);
            Add(f[2], point);
            var dash = Array.IndexOf(f, "-");
            if (dash > 0 && dash + 2 < f.Length && f[dash + 2].StartsWith("/dev/", StringComparison.Ordinal))
            {
                var source = f[dash + 2].Replace("\\040", " ", StringComparison.Ordinal);
                try
                {
                    source = File.ResolveLinkTarget(source, returnFinalTarget: true)?.FullName ?? source;
                }
                catch (IOException)
                {
                }

                Add("name:" + Path.GetFileName(source), point);
            }
        }

        return map;
    }

    private static HashSet<string> ReadSwaps(string proc) =>
        [.. ReadLines(Path.Combine(proc, "swaps")).Skip(1).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")];

    private static Dictionary<string, string> ReadUdev(string file)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ReadLines(file))
        {
            if (!line.StartsWith("E:", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 2) map[line[2..eq]] = line[(eq + 1)..];
        }

        return map;
    }

    private static IEnumerable<string> ReadLines(string file)
    {
        try
        {
            return File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? Read(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
