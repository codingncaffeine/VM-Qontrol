using System.Globalization;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Install;
using VmQontrol.Core.Import;
using VmQontrol.Core.Library;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.OsInfo;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

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
          create NAME [--iso FILE] [--os OS] [--disk-gib N] [--memory-mib N] [--cores N] [--bios] [--tpm] [--[no-]secure-boot]
                 [--easy-install --user NAME [--password P] [--edition N] [--key KEY] [--lean] [--drivers virtio-win.iso] [--first-logon CMD]]
                              Make a new machine in the library (the system is detected from --iso when --os is left out).
                              --easy-install installs Windows, Ubuntu, Fedora/RHEL or Debian with no questions
                              (Linux needs --password; --power-off-when-done stops the machine after installing).
                              --lean (Windows) removes the bundled promotions, games and ads while installing.
          template MACHINE [--undo]
                              Make a machine that is off into a template (never started, only cloned), or back.
          clone MACHINE NEW [--linked]
                              Copy a machine that is off. --linked shares its disks as they are now instead of copying
                              them (seconds, almost no space); both machines keep their own changes from then on.
          start MACHINE [--headless] [--paused]
                              Power the machine on. With a screen, the viewer opens.
          status MACHINE      Whether the machine is running, its process and its SSH port.
          shutdown MACHINE    Ask the guest to shut down (the power button).
          poweroff MACHINE    Turn the machine off at once (unsaved work in the guest is lost).
          suspend MACHINE     Save the running machine's state to its folder and turn it off; start carries on from it.
          discard-suspended MACHINE
                              Throw the saved state away: the next start begins afresh, as after a power cut.
          pause / resume / reset MACHINE
          screenshot MACHINE FILE.png
          version             Print the version and licence.
          help                Show this help.

        list, inspect and detect only read. MACHINE is a name in the library, a vm.json, a machine folder or a quickemu .conf.
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
                case Core.Runtime.SegmentServer.Argument:
                    // A private segment's address server, started by the first machine on it (never typed by hand).
                    paths.EnsureCreated();
                    Log.Minimum = LogLevel.Info; // a server of its own: its log is all there is to read
                    Log.Initialize("vmq-segment", paths.Logs);
                    return Core.Runtime.SegmentServer.Main(paths, args);
                case "version" or "--version" or "-V":
                    output.WriteLine($"vmq {BuildInfo.Stamp}");
                    output.WriteLine(BuildInfo.Licence);
                    return 0;
                case "help" or "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                case "list":
                    return List(args.Length > 1 ? args[1] : paths.DefaultLibrary, paths, output);
                case "inspect" when args.Length > 1:
                    return Inspect(args[1], paths, host(), output, error);
                case "detect" when args.Length > 1:
                    return Detect(args[1], output, error);
                case "create" when args.Length > 1:
                    return Create(args[1..], paths, host, output, error);
                case "template" when args.Length > 1:
                    return Template(args[1], !args.Contains("--undo"), paths, output, error);
                case "clone" when args.Length > 2:
                    return Clone(args[1], args[2], args.Contains("--linked"), paths, output, error);
                case "start" when args.Length > 1:
                    return Start(args[1], args.Contains("--headless"), args.Contains("--paused"), paths, host, output, error);
                case "status" when args.Length > 1:
                    return Control(args[1], paths, output, error, async r =>
                    {
                        output.WriteLine($"{r.Name}: {await MachineRunner.StatusAsync(r).ConfigureAwait(false)} (process {r.Pid}){(r.SshPort is { } p && Resolve(args[1], paths)?.Machine is { } sm ? $", SSH: {sm.Guest.SshCommand(p)}" : "")}");
                        // The guest's own addresses, when its agent answers (it is absent until guest tools are installed).
                        var addresses = await new GuestAgentClient(r.Paths.Agent).AddressesAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                        if (addresses is { Count: > 0 }) output.WriteLine($"  Guest addresses: {string.Join(", ", addresses.Select(a => $"{a.Address} ({a.Interface})"))}");
                        else if (addresses is null) output.WriteLine("  Guest agent: not answering (install the guest tools to see the guest's address)");
                    }, notRunning: 0);
                case "shutdown" when args.Length > 1:
                    return Control(args[1], paths, output, error, r => MachineRunner.ShutDownAsync(r));
                case "poweroff" when args.Length > 1:
                    return Control(args[1], paths, output, error, async r =>
                    {
                        await MachineRunner.PowerOffAsync(r).ConfigureAwait(false);
                        await MachineRunner.WaitForExitAsync(r, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        // Clears the runtime folder the machine left.
                        if (Resolve(args[1], paths)?.Machine is { } stopped) MachineRunner.Find(stopped, paths);
                    });
                case "suspend" when args.Length > 1:
                    return Control(args[1], paths, output, error, async r =>
                    {
                        await MachineSuspend.SuspendAsync(r, new Progress<string>(output.WriteLine)).ConfigureAwait(false);
                        output.WriteLine($"{r.Name} is suspended; 'vmq start {args[1]}' carries on from here.");
                    });
                case "discard-suspended" when args.Length > 1:
                    return DiscardSuspended(args[1], paths, output, error);
                case "pause" when args.Length > 1:
                    return Control(args[1], paths, output, error, r => MachineRunner.PauseAsync(r));
                case "resume" when args.Length > 1:
                    return Control(args[1], paths, output, error, r => MachineRunner.ResumeAsync(r));
                case "reset" when args.Length > 1:
                    return Control(args[1], paths, output, error, r => MachineRunner.ResetAsync(r));
                case "screenshot" when args.Length > 2:
                    return Control(args[1], paths, output, error, async r =>
                    {
                        await MachineRunner.ScreenshotAsync(r, args[2]).ConfigureAwait(false);
                        output.WriteLine($"Saved {Path.GetFullPath(args[2])}");
                    });
                case "inspect" or "detect" or "create" or "clone" or "template" or "start" or "status" or "shutdown" or "poweroff" or "suspend" or "discard-suspended" or "pause" or "resume" or "reset" or "screenshot":
                    error.WriteLine($"vmq {command}: something is missing ({(command == "detect" ? "the disc image" : command == "screenshot" ? "the machine and the file" : command == "clone" ? "the machine and the new name" : command == "create" ? "the name" : "the machine")}). Run 'vmq help' for the list.");
                    return 2;
                default:
                    error.WriteLine($"vmq: unknown command '{command}'. Run 'vmq help' for the list.");
                    return 2;
            }
        }
        catch (StartRefusedException ex)
        {
            foreach (var reason in ex.Reasons) error.WriteLine($"vmq {command}: {reason}");
            return 1;
        }
        catch (QmpException ex)
        {
            error.WriteLine($"vmq {command}: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Net.Sockets.SocketException)
        {
            error.WriteLine($"vmq {command}: {ex.Message}");
            return 1;
        }
    }

    private static int List(string root, QontrolPaths paths, TextWriter output)
    {
        var entries = LibraryScanner.Scan(root, paths.State);
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
        // Windows discs say exactly which release and editions they hold, beyond what their label can; a disc built from Microsoft's
        // download has a label osinfo-db does not know, so the disc itself is read first.
        if (WindowsMedia.Inspect(file) is { } windows)
        {
            output.WriteLine($"System:    {windows.Description}, {windows.Architecture}");
            foreach (var e in windows.Editions) output.WriteLine($"  Edition {e.Index}: {e.DisplayName}");
            output.WriteLine($"Profile:   {(windows.Server ? "windows-server" : "windows")} {windows.Release}");
            return 0;
        }

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

    private static int Create(string[] args, QontrolPaths paths, Func<HostCapabilities> host, TextWriter output, TextWriter error)
    {
        var name = args[0];
        string? Option(string flag) => Array.IndexOf(args, flag) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        long? Number(string flag) => long.TryParse(Option(flag), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (Directory.Exists(System.IO.Path.Combine(paths.DefaultLibrary, name)))
        {
            error.WriteLine($"vmq create: {System.IO.Path.Combine(paths.DefaultLibrary, name)} already exists.");
            return 1;
        }

        var iso = Option("--iso") is { } f ? System.IO.Path.GetFullPath(f) : null;
        var guest = new GuestConfig { Os = Option("--os") ?? "linux" };
        if (iso is not null && Option("--os") is null)
        {
            if (IsoVolume.Read(iso) is not { } volume)
            {
                error.WriteLine($"vmq create: {iso} is not a disc image.");
                return 1;
            }

            if (OsInfoDb.LoadInstalled()?.Identify(volume) is { } match)
            {
                guest = GuestMapping.ToGuest(match.Os);
                output.WriteLine($"Detected {match.Os.Name}{(match.Ambiguous.Count > 0 ? $" (or {string.Join(", ", match.Ambiguous.Select(a => a.Name))}; pass --os to choose)" : "")}.");
            }
        }

        if (iso is not null && Option("--os") is null && WindowsMedia.Inspect(iso) is { } windows)
        {
            guest = new GuestConfig { Os = windows.Server ? "windows-server" : "windows", Release = windows.Release, OsInfoId = guest.OsInfoId };
            output.WriteLine($"The disc holds {windows.Description}.");
        }

        var isWindows = guest.Os is "windows" or "windows-server";
        WindowsInstallChoices? easy = null;
        LinuxInstallChoices? linuxEasy = null;
        if (args.Contains("--easy-install") && !isWindows)
        {
            if (iso is null || Option("--user") is not { } linuxUser || Option("--password") is not { Length: > 0 } linuxPassword)
            {
                error.WriteLine("vmq create: Linux Easy Install needs --iso, --user NAME and --password P (the installers make no account without one).");
                return 2;
            }

            if (LinuxEasyInstall.Detect(iso) == LinuxInstaller.None)
            {
                error.WriteLine("vmq create: Easy Install does not know this disc's installer (it knows Ubuntu, the Fedora/RHEL family and Debian).");
                return 2;
            }

            linuxEasy = new LinuxInstallChoices
            {
                UserName = linuxUser,
                Password = linuxPassword,
                HostName = LinuxEasyInstall.HostName(name),
                TimeZone = TimeZoneInfo.Local.Id.Contains('/') ? TimeZoneInfo.Local.Id : "UTC",
                PowerOffWhenDone = args.Contains("--power-off-when-done"),
            };
        }
        else if (args.Contains("--easy-install"))
        {
            if (Option("--user") is not { } user)
            {
                error.WriteLine("vmq create: --easy-install needs --user NAME.");
                return 2;
            }

            easy = new WindowsInstallChoices
            {
                UserName = user,
                Password = Option("--password") ?? "",
                ComputerName = WindowsAnswerFile.ComputerName(name),
                EditionIndex = (int)(Number("--edition") ?? 1),
                EditionName = iso is not null ? WindowsMedia.Inspect(iso)?.Editions.FirstOrDefault(e => e.Index == (int)(Number("--edition") ?? 1))?.Name ?? "" : "",
                ConsumerDisc = iso is null || WindowsMedia.Inspect(iso)?.ConsumerDisc != false,
                ProductKey = Option("--key") ?? "",
                FirstLogonCommands = Option("--first-logon") is { } cmd ? [cmd] : [],
                Lean = args.Contains("--lean"),
            };
        }

        if (easy is not null && !WindowsAnswerFile.HasKey(easy))
        {
            error.WriteLine($"vmq create: {(easy.EditionName.Length > 0 ? easy.EditionName : "This edition")} has no published setup key; pass --key YOUR-KEY or choose a Pro edition with --edition.");
            return 2;
        }

        var (_, dir) = MachineFactory.Create(new NewMachineRequest
        {
            Name = name,
            LibraryRoot = paths.DefaultLibrary,
            Guest = guest,
            InstallerIso = iso,
            DriversIso = Option("--drivers") is { } drv ? System.IO.Path.GetFullPath(drv) : null,
            MemoryMiB = Number("--memory-mib"),
            Cores = (int?)Number("--cores"),
            Firmware = args.Contains("--bios") ? FirmwareKind.Bios : FirmwareKind.Uefi,
            Tpm = args.Contains("--tpm") || (isWindows && guest.Release == "11"),
            SecureBoot = !args.Contains("--bios") && !args.Contains("--no-secure-boot") && (args.Contains("--secure-boot") || Recommendations.SecureBoot(guest, host())),
            DiskGiB = Number("--disk-gib") ?? (isWindows ? 64 : guest.Os == "macos" ? 128 : 32),
            WindowsEasyInstall = easy,
            LinuxEasyInstall = linuxEasy,
        });
        output.WriteLine($"Created {name} in {dir}.");
        return 0;
    }

    private static int Template(string target, bool template, QontrolPaths paths, TextWriter output, TextWriter error)
    {
        var entry = Resolve(target, paths);
        if (entry?.Machine is not { } m)
        {
            error.WriteLine($"vmq template: no machine called '{target}'.");
            return 1;
        }

        if (MachineRunner.Find(m, paths) is not null)
        {
            error.WriteLine($"vmq template: power {m.Name} off first.");
            return 1;
        }

        try
        {
            MachineLifecycle.SetTemplate(m, entry.Directory, template);
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine($"vmq template: {ex.Message}");
            return 1;
        }

        output.WriteLine(template ? $"{m.Name} is a template: 'vmq clone {target} NEW --linked' makes machines from it." : $"{m.Name} is a machine again.");
        return 0;
    }

    private static int DiscardSuspended(string target, QontrolPaths paths, TextWriter output, TextWriter error)
    {
        var entry = Resolve(target, paths);
        if (entry?.Machine is not { } m)
        {
            error.WriteLine($"vmq discard-suspended: no machine called '{target}'.");
            return 1;
        }

        if (!MachineSuspend.IsSuspended(entry.Directory))
        {
            output.WriteLine($"{m.Name} is not suspended.");
            return 0;
        }

        MachineSuspend.Discard(entry.Directory);
        output.WriteLine($"{m.Name}'s saved state is gone; it will start afresh.");
        return 0;
    }

    private static int Clone(string target, string name, bool linked, QontrolPaths paths, TextWriter output, TextWriter error)
    {
        var entry = Resolve(target, paths);
        if (entry?.Machine is not { } m)
        {
            error.WriteLine($"vmq clone: no machine called '{target}'.");
            return 1;
        }

        if (MachineRunner.Find(m, paths) is not null)
        {
            error.WriteLine($"vmq clone: power {m.Name} off first; its disks must not change while they are cloned.");
            return 1;
        }

        var progress = new Progress<string>(output.WriteLine);
        var dir = linked
            ? MachineLifecycle.LinkedCloneAsync(m, entry.Directory, paths.DefaultLibrary, name, progress).GetAwaiter().GetResult().Directory
            : MachineLifecycle.CloneAsync(m, entry.Directory, paths.DefaultLibrary, name, progress).GetAwaiter().GetResult().Directory;
        output.WriteLine($"Created {name} in {dir}{(linked ? $", sharing {m.Name}'s disks as they were" : "")}.");
        return 0;
    }

    private static int Start(string target, bool headless, bool paused, QontrolPaths paths, Func<HostCapabilities> host, TextWriter output, TextWriter error)
    {
        var entry = Resolve(target, paths);
        if (entry?.Machine is not { } m)
        {
            error.WriteLine($"vmq start: no machine called '{target}'.");
            return 1;
        }

        var running = MachineRunner.StartAsync(m, entry.Directory, paths, host(), new StartOptions { Headless = headless, Paused = paused }).GetAwaiter().GetResult();
        output.WriteLine($"{m.Name} is running (process {running.Pid}){(running.SshPort is { } p ? $"; SSH: {m.Guest.SshCommand(p)}" : "")}.");
        if (!running.BootAssist.IsCompleted)
        {
            output.WriteLine("Starting the installer (pressing a key for the disc's boot prompt)…");
            running.BootAssist.GetAwaiter().GetResult();
        }
        if (!headless && m.Display.Backend == DisplayBackend.Spice)
        {
            var viewer = ViewerLauncher.Open(m, running);
            output.WriteLine(viewer is null ? "No SPICE viewer is installed (spicy or remote-viewer); install one to see the screen." : $"Opened {viewer}.");
        }

        return 0;
    }

    private static int Control(string target, QontrolPaths paths, TextWriter output, TextWriter error, Func<RunningMachine, Task> action, int notRunning = 1)
    {
        var entry = Resolve(target, paths);
        if (entry?.Machine is not { } m)
        {
            error.WriteLine($"vmq: no machine called '{target}'.");
            return 1;
        }

        if (MachineRunner.Find(m, paths) is not { } running)
        {
            (notRunning == 0 ? output : error).WriteLine($"{m.Name}: {(MachineSuspend.IsSuspended(entry.Directory) ? "suspended" : "off")}");
            return notRunning;
        }

        action(running).GetAwaiter().GetResult();
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
        return LibraryScanner.Scan(paths.DefaultLibrary, paths.State).FirstOrDefault(e => string.Equals(e.Machine?.Name, target, StringComparison.OrdinalIgnoreCase));
    }

    private static string Quote(string s) => s.Any(c => c is ' ' or '(' or ')' or '\'' or '"' or '$') ? $"'{s.Replace("'", "'\\''", StringComparison.Ordinal)}'" : s;
}
