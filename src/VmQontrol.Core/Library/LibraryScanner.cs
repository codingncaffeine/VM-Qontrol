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
    /// <param name="root">The library folder.</param>
    /// <param name="stateDirectory">VM Qontrol's state folder: a quickemu machine's settings changed in VM Qontrol are kept
    /// there (machines/ID/vm.json), so its .conf is never written, and they replace what the .conf says.</param>
    public static IReadOnlyList<LibraryEntry> Scan(string root, string? stateDirectory = null)
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
                // A .conf VM Qontrol wrote for one of its own machines (Export, quickemu .conf) is that machine, listed
                // once, from its vm.json: two entries on one disk could run it twice.
                if (File.Exists(System.IO.Path.Combine(import.MachineDirectory, MachineStore.FileName))) continue;
                var machine = import.Machine;
                if (stateDirectory is not null && System.IO.Path.Combine(stateDirectory, "machines", machine.Id, MachineStore.FileName) is var saved && File.Exists(saved))
                    machine = MachineStore.Load(saved) with { Id = machine.Id, ImportedFrom = machine.ImportedFrom };
                entries.Add(new LibraryEntry(MachineKind.Quickemu, conf, import.MachineDirectory, machine, import.Notes, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                entries.Add(new LibraryEntry(MachineKind.Quickemu, conf, root, null, [], ex.Message));
            }
        }

        return entries;
    }

    /// <summary>What is missing for a library entry's machine to start (see <see cref="MachineChecks.Problems"/>).</summary>
    public static IReadOnlyList<string> MissingFiles(LibraryEntry entry) =>
        entry.Machine is { } m ? MachineChecks.Problems(m, entry.Directory) : [];
}
