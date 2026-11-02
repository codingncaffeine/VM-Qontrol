using VmQontrol.Core.Import;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Library;

public enum MachineKind
{
    /// <summary>Made by VM Qontrol: a folder holding vm.json.</summary>
    Native,

    /// <summary>A quickemu .conf, read in place and never written.</summary>
    Quickemu,
}

public sealed record LibraryEntry(MachineKind Kind, string Path, string Directory, MachineConfig? Machine, IReadOnlyList<string> Notes, string? Error);

/// <summary>Finds the machines in a library folder: VM Qontrol machines (NAME/vm.json) and quickemu machines (NAME.conf). Reads only.</summary>
public static class LibraryScanner
{
    public static IReadOnlyList<LibraryEntry> Scan(string root)
    {
        var entries = new List<LibraryEntry>();
        if (!System.IO.Directory.Exists(root)) return entries;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var file = System.IO.Path.Combine(dir, MachineStore.FileName);
            if (!File.Exists(file)) continue;
            try
            {
                entries.Add(new LibraryEntry(MachineKind.Native, file, dir, MachineStore.Load(file), [], null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                entries.Add(new LibraryEntry(MachineKind.Native, file, dir, null, [], ex.Message));
            }
        }

        foreach (var conf in System.IO.Directory.EnumerateFiles(root, "*.conf").Order(StringComparer.Ordinal))
        {
            try
            {
                var import = QuickemuImporter.Import(conf);
                entries.Add(new LibraryEntry(MachineKind.Quickemu, conf, import.MachineDirectory, import.Machine, import.Notes, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entries.Add(new LibraryEntry(MachineKind.Quickemu, conf, root, null, [], ex.Message));
            }
        }

        return entries;
    }

    /// <summary>
    /// What is missing for a machine to start: its disk may be made at first power on, but an installer, a firmware file or
    /// a boot loader that is not there means it cannot boot. Each problem is one plain sentence.
    /// </summary>
    public static IReadOnlyList<string> MissingFiles(LibraryEntry entry)
    {
        var problems = new List<string>();
        if (entry.Machine is not { } m) return problems;
        string Full(string f) => System.IO.Path.IsPathRooted(f) ? f : System.IO.Path.Combine(entry.Directory, f);
        var diskMissing = m.Disks.Count == 0 || !File.Exists(Full(m.Disks[0].File));
        foreach (var media in m.Media)
        {
            var path = Full(media.File);
            if (media.Role == MediaRole.Unattended) continue;
            if (!File.Exists(path))
            {
                if (media.Role is MediaRole.Installer or MediaRole.Recovery && !diskMissing) continue;
                problems.Add($"The {Describe(media.Role)} {path} is missing.");
            }
            else if (media.Role is MediaRole.Installer or MediaRole.Drivers && System.IO.Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase)
                     && new FileInfo(path).Length > 0 && Media.IsoVolume.Read(path) is null)
            {
                problems.Add($"The {Describe(media.Role)} {path} is not a disc image (it may be an error page saved by a failed download).");
            }
        }

        if (diskMissing && !m.Media.Any(x => x.Role is MediaRole.Installer or MediaRole.Recovery && File.Exists(Full(x.File))))
            problems.Add("The machine has no disk yet and nothing to install from.");
        return problems;
    }

    private static string Describe(MediaRole role) => role switch
    {
        MediaRole.Installer => "installer disc",
        MediaRole.Drivers => "driver disc",
        MediaRole.Recovery => "recovery image",
        MediaRole.Bootloader => "boot loader",
        MediaRole.Floppy => "floppy image",
        _ => "file",
    };
}
