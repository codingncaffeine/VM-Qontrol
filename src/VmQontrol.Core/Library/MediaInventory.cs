using System.Text.Json.Nodes;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Library;

public enum MediaKind
{
    /// <summary>A machine's hard disk (or any disk image).</summary>
    Disk,

    /// <summary>An installer or driver disc (.iso).</summary>
    Disc,

    /// <summary>A frozen disk that linked clones are layered on (the library's .bases folder).</summary>
    Base,
}

public enum MediaStatus
{
    InUse,

    /// <summary>In a library folder, but no machine uses it: space that can be freed.</summary>
    Unused,

    /// <summary>A machine names it, but it is not there.</summary>
    Missing,
}

/// <summary>One disk or disc: where, how big (as the guest sees it and on this computer's drive), who uses it.</summary>
public sealed record MediaItem(string Path, MediaKind Kind, string Format, long? VirtualBytes, long? ActualBytes, IReadOnlyList<string> UsedBy, string? Backing, MediaStatus Status);

/// <summary>
/// Every disk and disc of the library (VirtualBox's Virtual Media Manager): the machines' disks, the bases their linked
/// clones are layered on, their discs, the shared discs (guest tools), and image files in library folders that nothing
/// uses. A machine's own working files (firmware variables, discard-session overlays) are never offered as unused.
/// Reads files and runs qemu-img: call it on a worker.
/// </summary>
public static class MediaInventory
{
    private static readonly string[] DiskExtensions = [".qcow2", ".img", ".raw", ".vmdk", ".vdi", ".vhd", ".vhdx"];

    private static readonly string[] DiscExtensions = [".iso"];

    public static async Task<IReadOnlyList<MediaItem>> ScanAsync(IReadOnlyList<LibraryEntry> machines, IReadOnlyList<string> roots, IReadOnlyList<string> sharedDiscs, CancellationToken ct = default)
    {
        var users = new Dictionary<string, (MediaKind Kind, List<string> Names)>(StringComparer.Ordinal);
        var internalFiles = new HashSet<string>(StringComparer.Ordinal);
        var internalFolders = new List<string>();
        void Use(string path, MediaKind kind, string who)
        {
            var full = Path.GetFullPath(path);
            if (!users.TryGetValue(full, out var u)) users[full] = u = (kind, []);
            if (!u.Names.Contains(who)) u.Names.Add(who);
        }

        foreach (var entry in machines)
        {
            if (entry.Machine is not { } m) continue;
            string Resolve(string f) => Path.IsPathRooted(f) ? f : Path.Combine(entry.Directory, f);
            foreach (var disk in m.Disks)
            {
                var file = Resolve(disk.File);
                Use(file, MediaKind.Disk, m.Name);
                if (File.Exists(file))
                    foreach (var below in DiskState.Chain(file).Skip(1)) Use(below, MediaKind.Base, $"{m.Name} (linked clone)");
            }

            foreach (var media in m.Media)
                Use(Resolve(media.File), DiscExtensions.Contains(Path.GetExtension(media.File).ToLowerInvariant()) ? MediaKind.Disc : MediaKind.Disk, m.Name);
            internalFiles.Add(Path.GetFullPath(Resolve(m.Firmware.VarsFile)));
            internalFolders.Add(Path.GetFullPath(DiscardSession.Folder(entry.Directory)) + Path.DirectorySeparatorChar);
        }

        foreach (var disc in sharedDiscs.Where(File.Exists)) Use(disc, MediaKind.Disc, "every Windows machine (guest tools)");

        // Image files in the library that nothing names: unused.
        var found = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, MaxRecursionDepth = 4 };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!DiskExtensions.Contains(ext) && !DiscExtensions.Contains(ext)) continue;
                var full = Path.GetFullPath(file);
                if (users.ContainsKey(full) || internalFiles.Contains(full) || internalFolders.Any(f => full.StartsWith(f, StringComparison.Ordinal))) continue;
                found.Add(full);
            }
        }

        var all = users.Select(u => (Path: u.Key, u.Value.Kind, Users: (IReadOnlyList<string>)u.Value.Names)).Concat(found.Select(f => (Path: f,
            Kind: f.Contains($"{Path.DirectorySeparatorChar}{MachineLifecycle.BasesFolder}{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? MediaKind.Base
                : DiscExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) ? MediaKind.Disc : MediaKind.Disk,
            Users: (IReadOnlyList<string>)[]))).ToList();
        using var limit = new SemaphoreSlim(4);
        var items = await Task.WhenAll(all.Select(async a =>
        {
            await limit.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await DescribeAsync(a.Path, a.Kind, a.Users, ct).ConfigureAwait(false);
            }
            finally
            {
                limit.Release();
            }
        })).ConfigureAwait(false);
        return items.OrderBy(i => i.Status).ThenBy(i => i.Kind).ThenByDescending(i => i.ActualBytes ?? 0).ToList();
    }

    private static async Task<MediaItem> DescribeAsync(string path, MediaKind kind, IReadOnlyList<string> users, CancellationToken ct)
    {
        if (!File.Exists(path)) return new MediaItem(path, kind, "", null, null, users, null, MediaStatus.Missing);
        var status = users.Count == 0 ? MediaStatus.Unused : MediaStatus.InUse;
        if (kind == MediaKind.Disc) return new MediaItem(path, kind, "iso", new FileInfo(path).Length, new FileInfo(path).Length, users, null, status);
        try
        {
            var info = await QemuImg.InfoAsync(path, ct).ConfigureAwait(false);
            return new MediaItem(path, kind, info["format"]?.GetValue<string>() ?? "", Number(info["virtual-size"]), Number(info["actual-size"]),
                users, info["full-backing-filename"]?.GetValue<string>() ?? info["backing-filename"]?.GetValue<string>(), status);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // Unreadable to qemu-img (damaged, or not an image at all): still listed, with the size on the drive.
            return new MediaItem(path, kind, "", null, new FileInfo(path).Length, users, null, status);
        }

        static long? Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : null;
    }
}
