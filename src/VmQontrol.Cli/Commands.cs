using System.Globalization;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Import;
using VmQontrol.Core.Library;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.OsInfo;
using VmQontrol.Core.Qemu;

namespace VmQontrol.Cli;

/// <summary>The <c>vmq</c> commands. Output goes to the writers passed in, so tests read exactly what a user sees.</summary>
internal static class Commands
{
    public const string Usage = """
        Usage: vmq <command> [options]

          list [FOLDER]       List the machines in a library folder (default: the library).
          inspect MACHINE     Show a machine's settings, anything missing, and the QEMU command it would start.
                              MACHINE is a quickemu .conf, a vm.json, a machine folder, or a name in the library.
          detect FILE.iso     Name the operating system on an installer disc.
          version             Print the version and licence.
          help                Show this help.

        Nothing here starts, changes or writes a machine.
        VMQONTROL_HOME=DIR keeps every setting, log and machine under DIR instead of the usual folders.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error, QontrolPaths? paths = null, Func<HostCapabilities>? host = null)
    {
        paths ??= QontrolPaths.ForCurrentUser();
        host ??= () => HostProbe.Probe();
        var command = args.Length == 0 ? "help" : args[0];
        try
        {
            switch (command)
            {
                case "version" or "--version" or "-V":
                    output.WriteLine($"vmq {BuildInfo.Stamp}");
                    output.WriteLine(BuildInfo.Licence);
                    return 0;
                case "help" or "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                case "list":
                    return List(args.Length > 1 ? args[1] : paths.DefaultLibrary, output);
                case "inspect" when args.Length > 1:
                    return Inspect(args[1], paths, host(), output, error);
                case "detect" when args.Length > 1:
                    return Detect(args[1], output, error);
                case "inspect" or "detect":
                    error.WriteLine($"vmq {command}: say which {(command == "inspect" ? "machine" : "file")}. Run 'vmq help' for the list.");
                    return 2;
                default:
                    error.WriteLine($"vmq: unknown command '{command}'. Run 'vmq help' for the list.");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error.WriteLine($"vmq {command}: {ex.Message}");
            return 1;
        }
    }

    private static int List(string root, TextWriter output)
    {
        var entries = LibraryScanner.Scan(root);
        if (entries.Count == 0)
        {
            output.WriteLine($"No machines in {root}.");
            return 0;
        }

        foreach (var e in entries)
        {
            var name = e.Machine?.Name ?? System.IO.Path.GetFileName(e.Path);
            var guest = e.Machine is { } m ? $"{m.Guest.Os}{(m.Guest.Release.Length > 0 ? " " + m.Guest.Release : "")}" : "unreadable";
            var problems = LibraryScanner.MissingFiles(e).Count;
            output.WriteLine($"{name,-28} {guest,-18} {(e.Kind == MachineKind.Quickemu ? "quickemu" : "vm qontrol"),-10} {(problems > 0 ? $"{problems} problem(s)" : "ready")}");
        }

        return 0;
    }

    private static int Inspect(string target, QontrolPaths paths, HostCapabilities host, TextWriter output, TextWriter error)
    {
        var entry = Resolve(target, paths);
        if (entry?.Machine is not { } m)
        {
            error.WriteLine(entry?.Error is { } why ? $"vmq inspect: {why}" : $"vmq inspect: no machine called '{target}'.");
            return 1;
        }

        output.WriteLine($"{m.Name}  ({(entry.Kind == MachineKind.Quickemu ? "quickemu machine, read in place" : "VM Qontrol machine")})");
        output.WriteLine($"  Guest:     {m.Guest.Os}{(m.Guest.Release.Length > 0 ? " " + m.Guest.Release : "")}");
        output.WriteLine($"  Folder:    {entry.Directory}");
        foreach (var note in entry.Notes) output.WriteLine($"  Note:      {note}");
        var problems = LibraryScanner.MissingFiles(entry);
        foreach (var p in problems) output.WriteLine($"  Problem:   {p}");

        var diskPath = m.Disks.Count > 0 ? (System.IO.Path.IsPathRooted(m.Disks[0].File) ? m.Disks[0].File : System.IO.Path.Combine(entry.Directory, m.Disks[0].File)) : "";
        var launch = QemuCommandBuilder.Build(m, new LaunchContext
        {
            Host = host,
            MachineDirectory = entry.Directory,
            Runtime = new RuntimePaths(paths.RuntimeFor(m.Id)),
            SystemDiskUsed = DiskState.IsUsed(diskPath),
        });
        output.WriteLine($"  Memory:    {QemuCommandBuilder.MemoryArg(launch.MemoryMiB)}   Cores: {launch.Cores.ToString(CultureInfo.InvariantCulture)}");
        foreach (var w in launch.Warnings) output.WriteLine($"  Warning:   {w}");
        foreach (var e in launch.Errors) output.WriteLine($"  Cannot start: {e}");
        output.WriteLine("  Command:");
        output.WriteLine("    qemu-system-x86_64 \\");
        for (var i = 0; i < launch.Args.Count; i++)
        {
            var a = launch.Args[i];
            var next = i + 1 < launch.Args.Count && !launch.Args[i + 1].StartsWith('-') ? launch.Args[++i] : null;
            output.WriteLine($"      {a}{(next is null ? "" : " " + Quote(next))}{(i + 1 < launch.Args.Count ? " \\" : "")}");
        }

        return problems.Count > 0 || launch.Errors.Count > 0 ? 3 : 0;
    }

    private static int Detect(string file, TextWriter output, TextWriter error)
    {
        var iso = IsoVolume.Read(file);
        if (iso is null)
        {
            error.WriteLine($"vmq detect: {file} is not a disc image.");
            return 1;
        }

        output.WriteLine($"Volume:    {iso.VolumeId}{(iso.Bootable ? "  (bootable)" : "")}");
        if (iso.PublisherId.Length > 0) output.WriteLine($"Publisher: {iso.PublisherId}");
        var db = OsInfoDb.LoadInstalled();
        if (db is null)
        {
            output.WriteLine("The operating-system database (osinfo-db) is not installed, so the system cannot be named.");
            return 0;
        }

        if (db.Identify(iso) is not { } match)
        {
            output.WriteLine("No operating system in osinfo-db recognises this disc.");
            return 0;
        }

        output.WriteLine(match.Ambiguous.Count == 0
            ? $"System:    {match.Os.Name}"
            : $"System:    {match.Os.Name} or {string.Join(" or ", match.Ambiguous.Select(a => a.Name))} (the disc label fits each)");
        var guest = GuestMapping.ToGuest(match.Os);
        output.WriteLine($"Profile:   {guest.Os}{(guest.Release.Length > 0 ? " " + guest.Release : "")}");
        return 0;
    }

    private static LibraryEntry? Resolve(string target, QontrolPaths paths)
    {
        if (File.Exists(target) && target.EndsWith(".conf", StringComparison.Ordinal))
        {
            var import = QuickemuImporter.Import(target);
            return new LibraryEntry(MachineKind.Quickemu, System.IO.Path.GetFullPath(target), import.MachineDirectory, import.Machine, import.Notes, null);
        }

        var json = Directory.Exists(target) ? System.IO.Path.Combine(target, MachineStore.FileName) : target;
        if (File.Exists(json) && System.IO.Path.GetFileName(json) == MachineStore.FileName)
            return new LibraryEntry(MachineKind.Native, System.IO.Path.GetFullPath(json), System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(json))!, MachineStore.Load(json), [], null);
        return LibraryScanner.Scan(paths.DefaultLibrary).FirstOrDefault(e => string.Equals(e.Machine?.Name, target, StringComparison.OrdinalIgnoreCase));
    }

    private static string Quote(string s) => s.Any(c => c is ' ' or '(' or ')' or '\'' or '"' or '$') ? $"'{s.Replace("'", "'\\''", StringComparison.Ordinal)}'" : s;
}
