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
          keep-changes / discard-changes MACHINE
                              For a machine whose changes are not kept at power off (Settings → Hard disks): write the
                              last session's changes into its disks, or throw them away.
          discard-suspended MACHINE
                              Throw the saved state away: the next start begins afresh, as after a power cut.
          pause / resume / reset MACHINE
          screenshot MACHINE FILE.png
          exec MACHINE PROGRAM [ARG...]
                              Run a program inside the guest and print what it printed (needs the guest agent; Windows'
                              agent runs programs only when allowed). The exit code is the program's.
          copy-to MACHINE FILE GUEST-PATH / copy-from MACHINE GUEST-PATH FILE
                              Copy a file into or out of the guest through its agent, with no shared folder or network.
          cable MACHINE on|off
                              Plug the machine's network cable in or pull it out while it runs.
          capture MACHINE FILE.pcap / capture MACHINE --stop
                              Write every packet of the machine's network adapter to a file Wireshark opens.
          import FILE [--name NAME]
                      Copy a machine from VMware (.vmx), VirtualBox (.vbox), an OVF appliance (.ovf, .ova), libvirt
                      (.xml), a disk on its own, or a VM Qontrol export (.tar) into the library. The original is only read.
          support FILE.tar.gz Collect what someone helping you needs (system, QEMU, checks, logs, machine settings) into
                              one file, with your home folder, user name, notes and account names left out.
          doctor              Check this computer: what VM Qontrol needs, what is missing, and the command that installs it.
          version             Print the version and license.
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
                    output.WriteLine(BuildInfo.License);
                    return 0;
                case "help" or "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                case "import" when args.Length > 1:
                    return Import(args, paths, output, error);
                case "support" when args.Length > 1:
                {
                    var bundle = Path.GetFullPath(args[1]);
                    var probed = host();
                    var included = Core.Hosts.SupportBundle.Create(bundle, paths, probed, Core.Hosts.Doctor.Run(probed, Core.Hosts.Doctor.DetectThisDistro(), AppSettings.Load(paths).LibraryRoots[0]));
                    output.WriteLine($"Saved {bundle} ({included.Count} files; nothing personal: read it with tar -tzf before sending).");
                    return 0;
                }
                case "doctor":
                    return Doctor(paths, host(), output);
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
                        if (Resolve(args[1], paths) is { Machine: { } stopped } stoppedEntry)
                        {
                            MachineRunner.Find(stopped, paths);
                            if (stopped.OnPowerOff == DiscardMode.Discard && DiscardSession.Exists(stoppedEntry.Directory))
                            {
                                DiscardSession.Discard(stoppedEntry.Directory);
                                output.WriteLine($"{stopped.Name}: this session's changes were discarded.");
                            }
                            else if (DiscardSession.Exists(stoppedEntry.Directory))
                                output.WriteLine($"{stopped.Name}: this session's changes wait: 'vmq keep-changes {args[1]}' writes them into its disks, 'vmq discard-changes {args[1]}' throws them away.");
                        }
                    });
                case "suspend" when args.Length > 1:
                    return Control(args[1], paths, output, error, async r =>
                    {
                        await MachineSuspend.SuspendAsync(r, new Progress<string>(output.WriteLine)).ConfigureAwait(false);
                        output.WriteLine($"{r.Name} is suspended; 'vmq start {args[1]}' carries on from here.");
                    });
                case "keep-changes" or "discard-changes" when args.Length > 1:
                {
                    var entry = Resolve(args[1], paths);
                    if (entry?.Machine is not { } sm)
                    {
                        error.WriteLine($"vmq: no machine called '{args[1]}'.");
                        return 1;
                    }

                    if (MachineRunner.Find(sm, paths) is not null)
                    {
                        error.WriteLine($"{sm.Name} is running; power it off first.");
                        return 1;
                    }

                    if (MachineSuspend.IsSuspended(entry.Directory) || !DiscardSession.Exists(entry.Directory))
                    {
                        error.WriteLine(MachineSuspend.IsSuspended(entry.Directory) ? $"{sm.Name} is suspended in the middle of its session; resume it and power it off first." : $"{sm.Name} has no changes waiting.");
                        return 1;
                    }

                    if (command == "keep-changes") DiscardSession.KeepAsync(sm, entry.Directory).GetAwaiter().GetResult();
                    else DiscardSession.Discard(entry.Directory);
                    output.WriteLine(command == "keep-changes" ? $"{sm.Name}: the changes are written into its disks." : $"{sm.Name}: the changes are thrown away.");
                    return 0;
                }
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
                case "exec" when args.Length > 2:
                {
                    var code = 0;
                    var rc = Control(args[1], paths, output, error, async r =>
                    {
                        var agent = new GuestAgentClient(r.Paths.Agent);
                        var result = await agent.ExecAsync(args[2], args[3..], null, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                        if (result is null)
                        {
                            error.WriteLine($"vmq exec: {agent.LastError ?? "the guest agent refused"}");
                            code = 1;
                            return;
                        }

                        output.Write(result.Output);
                        error.Write(result.Errors);
                        if (result.TimedOut) error.WriteLine("vmq exec: the program was still running after 10 minutes; it carries on in the guest.");
                        code = result.ExitCode ?? 124;
                    });
                    return rc != 0 ? rc : code;
                }
                case "copy-to" when args.Length > 3:
                    return CopyGuest(args[1], args[2], args[3], toGuest: true, paths, output, error);
                case "copy-from" when args.Length > 3:
                    return CopyGuest(args[1], args[3], args[2], toGuest: false, paths, output, error);
                case "cable" when args.Length > 2 && args[2] is "on" or "off":
                    return Control(args[1], paths, output, error, async r =>
                    {
                        await using var qmp = await QmpClient.ConnectAsync(r.Paths.Control, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        await LiveNetwork.SetCableAsync((c, a) => qmp.ExecuteAsync(c, a), r.Paths, args[2] == "on").ConfigureAwait(false);
                        output.WriteLine(args[2] == "on" ? $"{r.Name}: the network cable is plugged in." : $"{r.Name}: the network cable is out; the guest sees no network until 'vmq cable {args[1]} on'.");
                    });
                case "capture" when args.Length > 2:
                    return Control(args[1], paths, output, error, async r =>
                    {
                        await using var qmp = await QmpClient.ConnectAsync(r.Paths.Control, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        if (args[2] == "--stop")
                        {
                            var file = LiveNetwork.CapturePath(r.Paths);
                            await LiveNetwork.StopCaptureAsync((c, a) => qmp.ExecuteAsync(c, a), r.Paths).ConfigureAwait(false);
                            output.WriteLine(file is null ? $"{r.Name}: the capture stopped." : $"{r.Name}: the capture stopped; {file} is complete.");
                            return;
                        }

                        await LiveNetwork.StartCaptureAsync((c, a) => qmp.ExecuteAsync(c, a), r.Paths, args[2]).ConfigureAwait(false);
                        output.WriteLine($"{r.Name}: capturing into {Path.GetFullPath(args[2])}; 'vmq capture {args[1]} --stop' finishes the file.");
                    });
                case "inspect" or "detect" or "create" or "clone" or "template" or "start" or "status" or "shutdown" or "poweroff" or "suspend" or "discard-suspended" or "pause" or "resume" or "reset" or "screenshot" or "exec" or "copy-to" or "copy-from" or "cable" or "capture":
                    error.WriteLine($"vmq {command}: something is missing ({(command == "detect" ? "the disc image" : command == "screenshot" ? "the machine and the file" : command == "exec" ? "the machine and the program" : command is "copy-to" or "copy-from" ? "the machine and both files" : command == "cable" ? "the machine and on or off" : command == "capture" ? "the machine and the file, or --stop" : command == "clone" ? "the machine and the new name" : command == "create" ? "the name" : "the machine")}). Run 'vmq help' for the list.");
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
            output.WriteLine("No operating system in osinfo-db recognizes this disc.");
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
            var viewer = ViewerLauncher.Open(m, running, paths);
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

    /// <summary>The host check, as the welcome page shows it: one line per check, the install command under what is missing. Exit 1 when something is missing.</summary>
    private static int Doctor(QontrolPaths paths, HostCapabilities host, TextWriter output)
    {
        var distro = Core.Hosts.Doctor.DetectThisDistro();
        var checks = Core.Hosts.Doctor.Run(host, distro, AppSettings.Load(paths).LibraryRoots[0]);
        foreach (var c in checks.OrderByDescending(c => c.Status))
        {
            var badge = c.Status switch { CheckStatus.Ok => "ok       ", CheckStatus.Optional => "optional ", CheckStatus.Warning => "attention", _ => "MISSING  " };
            output.WriteLine($"{badge}  {c.Title}: {c.Detail}");
            if (c.Fix is { } fix && c.Status != CheckStatus.Ok) output.WriteLine($"           {fix}");
        }

        var missing = checks.Count(c => c.Status == CheckStatus.Missing);
        var system = Core.Hosts.Doctor.ThisSystemName() ?? distro.ToString();
        output.WriteLine(missing == 0 ? $"Ready ({system})." : $"{missing} missing ({system}).");
        return missing == 0 ? 0 : 1;
    }

    private static int Import(string[] args, QontrolPaths paths, TextWriter output, TextWriter error)
    {
        paths.EnsureCreated();
        var file = Path.GetFullPath(args[1]);
        var root = AppSettings.Load(paths).LibraryRoots[0];
        Directory.CreateDirectory(root);
        try
        {
            if (file.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                var dir = MachineExport.UnpackAsync(file, root, CancellationToken.None).GetAwaiter().GetResult();
                output.WriteLine($"Brought in {Path.GetFileName(dir)} in {dir} (every file checked against its manifest).");
                return 0;
            }

            var f = Core.Import.MachineImporter.Read(file, root);
            if (Array.IndexOf(args, "--name") is var n and >= 0 && n + 1 < args.Length) f = f with { Name = args[n + 1] };
            foreach (var note in f.Notes) output.WriteLine($"note: {note}");
            if (f.Problems.Count > 0)
            {
                foreach (var p in f.Problems) error.WriteLine(p);
                return 1;
            }

            var last = -1;
            var progress = new SyncProgress(p =>
            {
                var step = (int)p.Percent / 10;
                if (step == last) return;
                last = step;
                output.WriteLine($"{p.Status} {p.Percent:0}%");
            });
            var (m, folder) = Core.Import.MachineImporter.ImportAsync(f, root, progress, CancellationToken.None).GetAwaiter().GetResult();
            output.WriteLine($"Imported {m.Name} from {f.Kind} in {folder}.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
        {
            error.WriteLine($"{Path.GetFileName(file)} was not imported: {ex.Message}");
            return 1;
        }
    }

    private sealed class SyncProgress(Action<Core.Import.ImportProgress> report) : IProgress<Core.Import.ImportProgress>
    {
        public void Report(Core.Import.ImportProgress value) => report(value);
    }

    /// <summary>Copies one file between this computer and the guest through the guest agent.</summary>
    private static int CopyGuest(string machine, string local, string guest, bool toGuest, QontrolPaths paths, TextWriter output, TextWriter error)
    {
        var failed = false;
        var rc = Control(machine, paths, output, error, async r =>
        {
            var agent = new GuestAgentClient(r.Paths.Agent);
            long? bytes;
            if (toGuest)
            {
                await using var source = File.OpenRead(local);
                bytes = await agent.WriteFileAsync(guest, source).ConfigureAwait(false);
            }
            else
            {
                var partial = local + ".part";
                await using (var target = File.Create(partial)) bytes = await agent.ReadFileAsync(guest, target).ConfigureAwait(false);
                if (bytes is null) File.Delete(partial);
                else File.Move(partial, local, overwrite: true);
            }

            if (bytes is null)
            {
                error.WriteLine($"vmq copy: {agent.LastError ?? "the guest agent refused"}");
                failed = true;
                return;
            }

            output.WriteLine(toGuest ? $"Copied {bytes:N0} bytes to {r.Name}: {guest}" : $"Copied {bytes:N0} bytes from {r.Name} to {Path.GetFullPath(local)}");
        });
        return rc != 0 ? rc : failed ? 1 : 0;
    }
}
