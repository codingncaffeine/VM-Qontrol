using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;

namespace VmQontrol.Core.Runtime;

/// <summary>Where one shared folder is in the guest, or why it is not there yet.</summary>
public sealed record ShareState(string Tag, string Name, string? Where, string? Problem);

/// <summary>What the share connector last did for a running machine (written to its runtime folder for the app).</summary>
public sealed record SharesReport(DateTimeOffset At, string Summary, bool Done, IReadOnlyList<ShareState> Shares)
{
    /// <summary>The guest user the files should belong to, as the guest reported it (Linux).</summary>
    public uint? Owner { get; init; }

    /// <summary>The guest is Windows (its setup is not repeated within one power on).</summary>
    public bool Windows { get; init; }
}

/// <summary>
/// D42: shared folders that appear in the machine by themselves, as VMware Tools does (/mnt/hgfs, a Windows drive). After
/// power on a helper of its own (<see cref="Argument"/>) waits for the guest agent, then sets the folders up inside the
/// guest through it: Linux gets a virtiofs mount under /mnt/shared and an fstab line (nofail, so every later boot mounts
/// them without this app); Windows gets WinFsp (copied in when missing), the virtio-fs launcher class, one drive letter
/// per folder named after it, and a start-up task that mounts them at each boot. Nothing is typed in the guest.
/// </summary>
public static class SharedFolders
{
    public const string Argument = "--connect-shares";

    /// <summary>Where Linux guests see the folders (VMware: /mnt/hgfs).</summary>
    public const string LinuxRoot = "/mnt/shared";

    /// <summary>virtio-fs mount tags are at most 36 bytes (virtiofs.exe on Windows refuses longer ones).</summary>
    public const int MaxTag = 36;

    /// <summary>WinFsp, which the Windows virtio-fs client runs on (GPLv3 with a FLOSS exception; fetched from its release, never bundled).</summary>
    public const string WinFspUrl = "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi";

    public const string WinFspSha256 = "073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A";

    /// <summary>The guest folder Windows keeps VM Qontrol's scripts and the WinFsp installer in.</summary>
    public const string WindowsFolder = @"C:\ProgramData\VMQontrol";

    /// <summary>The file in a running machine's runtime folder: the shares it started with (the devices QEMU has).</summary>
    public static string StartedFile(RuntimePaths runtime) => Path.Combine(runtime.Directory, "shares.json");

    /// <summary>The file in a running machine's runtime folder: what the connector last did.</summary>
    public static string ReportFile(RuntimePaths runtime) => Path.Combine(runtime.Directory, "shares-report.json");

    /// <summary>The guest account that owns the shared files: the first user every Linux distribution makes.</summary>
    public const uint GuestOwner = 1000;

    /// <summary>
    /// As VMware's shared folders: in the guest every file belongs to the machine's account (<paramref name="owner"/>),
    /// and what the guest makes, as any user and root too, is this account's file here. An unprivileged virtiofsd's
    /// namespace keeps this account's own ids, so --translate-uid shows this account's files as the owner and writes
    /// every guest id as this account. An older virtiofsd (no --translate-uid) maps this account to the owner in its
    /// namespace instead (the guest's root cannot make files then).
    /// </summary>
    public static IReadOnlyList<string> OwnerArgs(bool translates, (uint Uid, uint Gid) host, uint owner = GuestOwner) => translates
        ? [$"--translate-uid=host:{host.Uid}:{owner}:1", $"--translate-uid=squash-guest:0:{host.Uid}:4294967295",
           $"--translate-gid=host:{host.Gid}:{owner}:1", $"--translate-gid=squash-guest:0:{host.Gid}:4294967295"]
        : [$"--uid-map=:{owner}:{host.Uid}:1:", $"--gid-map=:{owner}:{host.Gid}:1:"];

    /// <summary>
    /// The guest's own mounter, put in by VM Qontrol when it makes the machine (cloud images): at every boot it mounts each
    /// folder the machine has at /mnt/shared/TAG, from the kernel's list of virtiofs tags (Linux 6.9+). It is what makes
    /// shared folders work where SELinux keeps the guest agent from mounting (Fedora, RHEL: virt_qemu_ga_t may not even
    /// run mount), and it covers folders shared later with nothing more to do.
    /// </summary>
    public const string GuestMounterPath = "/usr/local/sbin/vmqontrol-shares";

    public const string GuestMounter = """
        #!/bin/sh
        # Mounts every folder VM Qontrol shares with this machine at /mnt/shared/NAME (put in by VM Qontrol).
        umask 022
        for t in /sys/fs/virtiofs/*/tag; do
            [ -r "$t" ] || continue
            tag=$(cat "$t")
            case "$tag" in ''|*[!a-z0-9-]*) continue ;; esac
            mkdir -p "/mnt/shared/$tag"
            grep -q " /mnt/shared/$tag virtiofs " /proc/mounts || mount -t virtiofs "$tag" "/mnt/shared/$tag"
        done
        exit 0

        """;

    public const string GuestMounterUnit = """
        [Unit]
        Description=VM Qontrol shared folders
        After=local-fs.target
        ConditionVirtualization=vm

        [Service]
        Type=oneshot
        RemainAfterExit=yes
        ExecStart=/usr/local/sbin/vmqontrol-shares

        [Install]
        WantedBy=multi-user.target

        """;

    /// <summary>
    /// One shell line that puts the guest's own mounter in an installed system and enables it (an installer's last step:
    /// kickstart %post, autoinstall late-commands in the target, preseed in-target). Base64 keeps it free of quotes.
    /// </summary>
    public static string GuestMounterInstall =>
        $"mkdir -p /usr/local/sbin /etc/systemd/system && echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(GuestMounter))} | base64 -d > {GuestMounterPath} && chmod 755 {GuestMounterPath}"
        + $" && echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(GuestMounterUnit))} | base64 -d > /etc/systemd/system/vmqontrol-shares.service && systemctl enable vmqontrol-shares.service";

    /// <summary>The file in a machine's folder naming the guest user its shared files belong to (the connector learns it).</summary>
    public static string OwnerFile(string machineDirectory) => Path.Combine(machineDirectory, "shares-owner");

    public static uint? ReadOwner(string machineDirectory)
    {
        try
        {
            return uint.TryParse(File.ReadAllText(OwnerFile(machineDirectory)).Trim(), System.Globalization.CultureInfo.InvariantCulture, out var uid) && uid is > 0 and < 60000 ? uid : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A folder name made into a mount tag: lower-case ASCII letters, digits and dashes, at most 36 bytes.</summary>
    public static string SafeTag(string text)
    {
        var tag = new string(text.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (tag.Contains("--", StringComparison.Ordinal)) tag = tag.Replace("--", "-", StringComparison.Ordinal);
        tag = tag.Trim('-');
        if (tag.Length > MaxTag) tag = tag[..MaxTag].TrimEnd('-');
        return tag.Length == 0 ? "share" : tag;
    }

    /// <summary>The name a person knows the folder by (its own name on this computer).</summary>
    public static string DisplayName(ShareConfig share) =>
        Path.GetFileName(share.Path.TrimEnd('/')) is { Length: > 0 } name ? name : share.Tag;

    /// <summary>Where a Linux guest has the folder.</summary>
    public static string LinuxMount(ShareConfig share) => $"{LinuxRoot}/{SafeTag(share.Tag)}";

    /// <summary>Records the shares the machine starts with, for the connector (a share added later is not a device yet).</summary>
    public static void WriteStarted(RuntimePaths runtime, IReadOnlyList<ShareConfig> shares, uint owner = GuestOwner, string? account = null)
    {
        var list = new JsonArray([.. shares.Select(s => (JsonNode)new JsonObject { ["path"] = s.Path, ["tag"] = s.Tag, ["readOnly"] = s.ReadOnly })]);
        File.WriteAllText(StartedFile(runtime), new JsonObject { ["owner"] = owner, ["account"] = account, ["shares"] = list }.ToJsonString());
    }

    /// <summary>The shares the machine started with, the guest user their files were given, and the machine's account.</summary>
    public static (IReadOnlyList<ShareConfig> Shares, uint Owner, string? Account) ReadStarted(RuntimePaths runtime)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(StartedFile(runtime))) is not JsonObject o || o["shares"] is not JsonArray a) return ([], GuestOwner, null);
            return ([.. a.OfType<JsonObject>().Select(x => new ShareConfig { Path = x["path"]!.GetValue<string>(), Tag = x["tag"]!.GetValue<string>(), ReadOnly = x["readOnly"]?.GetValue<bool>() == true })],
                o["owner"]?.GetValue<uint>() ?? GuestOwner, o["account"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is IOException or JsonException or NullReferenceException or InvalidOperationException or FormatException)
        {
            return ([], GuestOwner, null);
        }
    }

    public static SharesReport? ReadReport(RuntimePaths runtime)
    {
        try
        {
            return JsonSerializer.Deserialize<SharesReport>(File.ReadAllText(ReportFile(runtime)), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static void WriteReport(RunningMachine running, QontrolPaths paths, SharesReport report)
    {
        var file = ReportFile(running.Paths);
        try
        {
            File.WriteAllText(file + ".new", JsonSerializer.Serialize(report, JsonOptions));
            File.Move(file + ".new", file, overwrite: true);
            var logs = Path.Combine(paths.State, "machines", running.Id); // MachineRunner.LogDirectory: the Log tab's folder
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "shares.log"),
                $"{report.At:yyyy-MM-dd HH:mm:ss} {report.Summary}{string.Concat(report.Shares.Select(s => $"\n  {s.Name} ({s.Tag}): {s.Where ?? s.Problem}"))}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The runtime folder goes when the machine stops; a report that cannot be written is nothing to act on.
        }
    }

    /// <summary>Starts the connector for a machine that has shares (VM Qontrol's own programs only; a test calls <see cref="ConnectAsync"/>).</summary>
    public static void EnsureConnector(RunningMachine running)
    {
        if (!Detached.CanStartSelf) return;
        if (!Detached.StartSelf([Argument, running.RuntimeDirectory], out var failure))
            Log.Warn($"{running.Name}: the shared folders will not appear in the machine by themselves ({failure}).");
    }

    /// <summary>The entry point for <see cref="Argument"/>: <c>--connect-shares RUNTIME-FOLDER</c>.</summary>
    public static int Main(QontrolPaths paths, string[] args)
    {
        try
        {
            if (args.Length < 2) return 2;
            var running = MachineRunner.ListRunning(paths).FirstOrDefault(r => Path.GetFullPath(r.RuntimeDirectory) == Path.GetFullPath(args[1]));
            if (running is null) return 0;
            WatchAsync(running, paths, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        finally
        {
            Log.Flush(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Tests: how often a guest with no agent yet is asked again.</summary>
    internal static TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Waits for the guest agent for as long as the machine runs (a guest may get its tools long after power on), then
    /// sets the folders up once. Returns the last report.
    /// </summary>
    public static async Task<SharesReport?> WatchAsync(RunningMachine running, QontrolPaths paths, CancellationToken ct)
    {
        var (shares, owner, account) = ReadStarted(running.Paths);
        if (shares.Count == 0) return null;
        var agent = new GuestAgentClient(running.Paths.Agent);
        var waiting = new SharesReport(DateTimeOffset.Now, "Waiting for the guest tools to answer in the machine.", false,
            [.. shares.Select(s => new ShareState(s.Tag, DisplayName(s), null, "waiting for the guest tools"))]);
        WriteReport(running, paths, waiting);
        while (!ct.IsCancellationRequested && MachineRunner.IsRunning(running))
        {
            if (await agent.PingAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            {
                var report = await ConnectAsync(agent, shares, paths, ct, account).ConfigureAwait(false);
                if (!report.Done)
                {
                    await Task.Delay(Poll, ct).ConfigureAwait(false);
                    continue;
                }

                // A first boot may still be setting itself up (cloud-init starts the guest's own mounter after the agent
                // answers): a folder not there yet is looked at again for a few minutes.
                for (var retry = 0; retry < 18 && !report.Windows && report.Shares.Any(x => x.Where is null) && MachineRunner.IsRunning(running); retry++)
                {
                    await Task.Delay(Poll * 2.5, ct).ConfigureAwait(false);
                    report = await ConnectAsync(agent, shares, paths, ct, account).ConfigureAwait(false);
                }

                if (report.Owner is { } learned && learned != owner)
                {
                    // virtiofsd gave the files to another guest user; from the next power on they are this one's.
                    try
                    {
                        File.WriteAllText(OwnerFile(running.MachineDirectory), learned.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }

                    report = report with { Summary = report.Summary + $" Their files belong to the machine's own account from its next power off and on (until then to user {owner})." };
                }

                WriteReport(running, paths, report);
                Log.Info($"{running.Name}: shared folders: {report.Summary}");
                // A guest that refused (no WinFsp download, an old kernel) is asked again only at the next power on.
                return report;
            }

            await Task.Delay(Poll, ct).ConfigureAwait(false);
        }

        return waiting;
    }

    /// <summary>Sets <paramref name="shares"/> up in the guest behind <paramref name="agent"/>, by the system it reports.</summary>
    public static async Task<SharesReport> ConnectAsync(GuestAgentClient agent, IReadOnlyList<ShareConfig> shares, QontrolPaths paths, CancellationToken ct, string? account = null)
    {
        var os = await agent.OsInfoAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        // No answer is not "Linux": the guest is busy or going down; the caller asks again.
        if (os is null) return new SharesReport(DateTimeOffset.Now, "Waiting for the guest tools to answer in the machine.", false,
            [.. shares.Select(s => new ShareState(s.Tag, DisplayName(s), null, "waiting for the guest tools"))]);
        var id = os["id"].Str() ?? "";
        var kernel = os["kernel-release"].Str() ?? "";
        if (id == "mswindows") return await ConnectWindowsAsync(agent, shares, paths, ct).ConfigureAwait(false) with { Windows = true };
        if (id is "freebsd" or "netbsd" or "openbsd")
            return Failed(shares, $"This system ({os["pretty-name"].Str() ?? id}) has no virtio-fs support; the folders cannot appear in it.");
        // Linux (every distribution's agent reports its os-release ID; an agent too old to report one runs Linux too).
        return await ConnectLinuxAsync(agent, shares, kernel, account, ct).ConfigureAwait(false);
    }

    private static SharesReport Failed(IReadOnlyList<ShareConfig> shares, string why) =>
        new(DateTimeOffset.Now, why, true, [.. shares.Select(s => new ShareState(s.Tag, DisplayName(s), null, why))]);

    /// <summary>
    /// The Linux setup, one POSIX sh script (systemd and busybox/OpenRC alike) run as root by the agent: VM Qontrol's own
    /// fstab lines replaced (marked "# vmqontrol"), each folder mounted now unless it already is. nofail keeps a boot
    /// without the device (the machine started with the share removed) from stopping; x-gvfs-show (util-linux mount only)
    /// puts the folder in GNOME's and other GIO file managers' side bar.
    /// </summary>
    public static string LinuxScript(IReadOnlyList<ShareConfig> shares, string? account = null)
    {
        var s = new StringBuilder();
        s.Append("set -u\n");
        // The agent may run it with no PATH at all (Fedora's qemu-ga): mount and modprobe are found by name.
        s.Append("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin; export PATH\n");
        // Alpine's agent runs with umask 077: /mnt/shared made by it was closed to every account but root.
        s.Append($"umask 022; mkdir -p {LinuxRoot}; chmod 755 {LinuxRoot}\n");
        s.Append("F=/etc/fstab; [ -e $F ] || : > $F\n");
        s.Append("if [ -d /run/systemd/system ] && mount -V 2>/dev/null | grep -q util-linux; then X=,x-gvfs-show; else X=; fi\n");
        s.Append("grep -v ' # vmqontrol$' $F > $F.vmqontrol\n");
        foreach (var share in shares)
        {
            var tag = SafeTag(share.Tag);
            s.Append($"echo \"{tag} {LinuxMount(share)} virtiofs defaults,nofail{(share.ReadOnly ? ",ro" : "")}$X 0 0 # vmqontrol\" >> $F.vmqontrol\n");
        }

        // Rewritten in place only when it changed (cat keeps the file's owner, mode and SELinux label).
        s.Append("if cmp -s $F $F.vmqontrol; then rm -f $F.vmqontrol; else cat $F.vmqontrol > $F && rm -f $F.vmqontrol; fi\n");
        // Whose the files are: the machine's account (a cloud image's own user may hold 1000), else its first person.
        var safeAccount = account is { Length: > 0 and <= 32 } a && a.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') && !a.StartsWith('-') ? a : null;
        s.Append($"o=$({(safeAccount is null ? "" : $"id -u {safeAccount} 2>/dev/null || ")}awk -F: '$3 >= 1000 && $3 < 60000 && $6 ~ /^\\/home\\// {{ print $3; exit }}' /etc/passwd); [ -n \"$o\" ] && echo \"owner=$o\"\n");
        s.Append("modprobe virtiofs 2>/dev/null\n");
        s.Append("grep -qw virtiofs /proc/filesystems || { echo 'nofs'; exit 0; }\n");
        s.Append("[ \"$(cat /sys/fs/selinux/enforce 2>/dev/null)\" = 1 ] && echo selinux\n");
        foreach (var share in shares)
        {
            var tag = SafeTag(share.Tag);
            var at = LinuxMount(share);
            s.Append($"mkdir -p {at}\n");
            s.Append($"if grep -q ' {at} virtiofs ' /proc/mounts; then echo 'ok {tag}'; ");
            s.Append($"elif e=$(mount -t virtiofs{(share.ReadOnly ? " -o ro" : "")} {tag} {at} 2>&1); then echo 'ok {tag}'; else echo \"fail {tag} $e\"; fi\n");
        }

        return s.ToString();
    }

    private static async Task<SharesReport> ConnectLinuxAsync(GuestAgentClient agent, IReadOnlyList<ShareConfig> shares, string kernel, string? account, CancellationToken ct)
    {
        var result = await agent.ExecAsync("/bin/sh", ["-c", LinuxScript(shares, account)], null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (result is null) return Failed(shares, $"The guest agent would not run the setup ({agent.LastError ?? "no answer"}).");
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Contains("nofs"))
            return Failed(shares, $"This Linux kernel ({kernel}) has no virtiofs; it arrived in Linux 5.4. A newer kernel shows the folders.");
        var selinux = lines.Contains("selinux");
        var states = shares.Select(s =>
        {
            var tag = SafeTag(s.Tag);
            if (lines.Contains($"ok {tag}")) return new ShareState(s.Tag, DisplayName(s), LinuxMount(s), null);
            if (selinux)
                return new ShareState(s.Tag, DisplayName(s), null,
                    $"SELinux keeps the guest tools from mounting in this system (machines VM Qontrol makes have their own mounter). Once, in the machine: sudo mkdir -p {LinuxMount(s)} && echo '{tag} {LinuxMount(s)} virtiofs defaults,nofail 0 0' | sudo tee -a /etc/fstab && sudo mount {LinuxMount(s)}");
            var fail = lines.FirstOrDefault(l => l.StartsWith($"fail {tag} ", StringComparison.Ordinal));
            return new ShareState(s.Tag, DisplayName(s), null, fail is null ? $"not mounted ({result.Errors.Trim()})" : fail[(tag.Length + 6)..]);
        }).ToList();
        var good = states.Count(x => x.Where is not null);
        var ownerLine = lines.FirstOrDefault(l => l.StartsWith("owner=", StringComparison.Ordinal));
        return new SharesReport(DateTimeOffset.Now,
            good == states.Count ? $"In the machine at {LinuxRoot}, mounted again at every start." : $"{good} of {states.Count} shared folders are in the machine.",
            true, states)
        {
            Owner = uint.TryParse(ownerLine?[6..], System.Globalization.CultureInfo.InvariantCulture, out var uid) && uid is > 0 and < 60000 ? uid : null,
        };
    }

    // ---- Windows -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The Windows setup, a PowerShell script run as SYSTEM by the agent. It finds virtiofs.exe (installed by the guest
    /// tools, or on the VirtIO driver disc, whose viofs driver it then installs), installs WinFsp from the copy put next to
    /// it when missing, turns the single-folder "VirtIO-FS Service" off and registers the WinFsp launcher class
    /// "virtiofs" instead (virtio-win's documented way to serve several folders), writes the mount script with one
    /// launcher instance per folder, named in Explorer after the folder, and runs it now and at every start (a task).
    /// Its last line is "result=ok" or "result=PROBLEM", and one "drive TAG X:" line per folder mounted.
    /// </summary>
    public static string WindowsScript(IReadOnlyList<ShareConfig> shares)
    {
        static string Ps(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        var tags = string.Join(",", shares.Select(s => Ps(SafeTag(s.Tag))));
        var names = string.Join(",", shares.Select(s => Ps(DisplayName(s))));
        return $$"""
            $ErrorActionPreference = 'Continue'
            $dir = {{Ps(WindowsFolder)}}
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $tags = @({{tags}})
            $names = @({{names}})
            function Done($what) { "result=$what"; exit 0 }

            # virtiofs.exe: the guest tools' service, their install folder, or the driver disc.
            $exe = $null
            $svc = Get-CimInstance Win32_Service -Filter "Name='VirtioFsSvc'" -ErrorAction SilentlyContinue
            if ($svc -and $svc.PathName -match '^"?([^"]+?\.exe)') { $exe = $Matches[1] }
            if (-not ($exe -and (Test-Path $exe))) {
                $exe = Get-ChildItem "$env:ProgramFiles\Virtio-Win", "$env:ProgramFiles\RedHat" -Recurse -Filter virtiofs.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
            }
            if (-not $exe) {
                $build = [Environment]::OSVersion.Version.Build
                $os = if ($build -ge 22000) { 'w11' } else { 'w10' }
                foreach ($d in (Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=5' -ErrorAction SilentlyContinue).DeviceID) {
                    foreach ($v in @($os, 'w10', '2k22', '2k19')) {
                        $src = "$d\viofs\$v\amd64"
                        if (Test-Path "$src\virtiofs.exe") {
                            pnputil.exe /add-driver "$src\viofs.inf" /install | Out-Null
                            Copy-Item "$src\virtiofs.exe" "$dir\virtiofs.exe" -Force
                            $exe = "$dir\virtiofs.exe"
                            break
                        }
                    }
                    if ($exe) { break }
                }
            }
            if (-not $exe) { Done 'no-virtiofs' }
            $device = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -match 'VEN_1AF4&DEV_(105A|1049)' } | Select-Object -First 1
            if (-not $device) { Done 'no-device' }
            if ($device.Status -ne 'OK') { Done 'no-driver' }

            # WinFsp.
            $key = 'HKLM:\SOFTWARE\WOW6432Node\WinFsp'
            $winfsp = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallDir
            if (-not $winfsp -and (Test-Path "$dir\winfsp.msi")) {
                Start-Process msiexec.exe -ArgumentList @('/i', "`"$dir\winfsp.msi`"", '/qn', '/norestart') -Wait
                $winfsp = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallDir
            }
            if (-not $winfsp) { Done 'no-winfsp' }
            $winfsp = $winfsp.TrimEnd('\')

            # One launcher class for every folder (what WinFsp's fsreg.bat writes); the single-folder service steps aside.
            if ($svc) { Stop-Service VirtioFsSvc -Force -ErrorAction SilentlyContinue; Set-Service VirtioFsSvc -StartupType Manual -ErrorAction SilentlyContinue }
            $class = "$key\Services\virtiofs"
            New-Item -Path $class -Force | Out-Null
            Set-ItemProperty $class Executable $exe
            Set-ItemProperty $class CommandLine '-t %1 -m %2 -F NTFS'
            Set-ItemProperty $class Security 'D:P(A;;RPWPLC;;;WD)'
            Set-ItemProperty $class JobControl 1 -Type DWord

            # Drive letters: kept from the last setup when still free, else the next free from Z: down.
            $old = @{}
            if (Test-Path "$dir\shares.txt") { foreach ($l in Get-Content "$dir\shares.txt") { $p = $l -split ' '; if ($p.Count -eq 2) { $old[$p[0]] = $p[1] } } }
            $launchctl = "$winfsp\bin\launchctl-x64.exe"
            foreach ($l in $old.Values) { & $launchctl stop virtiofs "vmq$l" | Out-Null }
            $used = @{}
            foreach ($d in (Get-CimInstance Win32_LogicalDisk).DeviceID) { $used[[string]$d[0]] = $true }
            $map = @()
            for ($i = 0; $i -lt $tags.Count; $i++) {
                $l = $old[$tags[$i]]
                if (-not $l -or $used[$l]) { $l = 90..68 | ForEach-Object { [string][char]$_ } | Where-Object { -not $used[$_] } | Select-Object -First 1 }
                if (-not $l) { continue }
                $used[$l] = $true
                $map += "$($tags[$i]) $l"
                $label = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\$l\DefaultLabel"
                New-Item -Path $label -Force | Out-Null
                Set-ItemProperty -Path $label -Name '(default)' -Value $names[$i]
            }
            Set-Content "$dir\shares.txt" $map

            # Mounted now and at every start (the launcher service may start after the task: it is waited for).
            $mount = @(
                ('$launchctl = ' + "'$launchctl'"),
                'for ($t = 0; $t -lt 60 -and (Get-Service WinFsp.Launcher -ErrorAction SilentlyContinue).Status -ne ''Running''; $t++) { Start-Service WinFsp.Launcher -ErrorAction SilentlyContinue; Start-Sleep 1 }',
                ('foreach ($line in Get-Content ''' + $dir + '\shares.txt'') { $p = $line -split '' ''; & $launchctl start virtiofs "vmq$($p[1])" $p[0] "$($p[1]):" | Out-Null }')
            ) # end of the mount script
            Set-Content "$dir\mount-shares.ps1" $mount
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$dir\mount-shares.ps1`""
            $trigger = New-ScheduledTaskTrigger -AtStartup
            Register-ScheduledTask -TaskName 'VM Qontrol shared folders' -Action $action -Trigger $trigger -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
            & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$dir\mount-shares.ps1"
            Start-Sleep 2
            foreach ($m in $map) { $p = $m -split ' '; if (Test-Path "$($p[1]):\") { "drive $($p[0]) $($p[1]):" } }
            Done 'ok'
            """;
    }

    private static async Task<SharesReport> ConnectWindowsAsync(GuestAgentClient agent, IReadOnlyList<ShareConfig> shares, QontrolPaths paths, CancellationToken ct)
    {
        await agent.ExecAsync("cmd.exe", ["/c", "mkdir", WindowsFolder], null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        // WinFsp goes in with the script; the script installs it only when the guest lacks it.
        if (await WinFspAsync(paths, ct).ConfigureAwait(false) is { } msi)
        {
            await using var file = File.OpenRead(msi);
            await agent.WriteFileAsync(WindowsFolder + @"\winfsp.msi", file, null, ct).ConfigureAwait(false);
        }

        var script = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(WindowsScript(shares).Replace("\n", "\r\n", StringComparison.Ordinal))).ToArray();
        if (await agent.WriteFileAsync(WindowsFolder + @"\setup-shares.ps1", new MemoryStream(script), null, ct).ConfigureAwait(false) is null)
            return Failed(shares, $"The setup could not be copied into Windows ({agent.LastError ?? "no answer"}).");
        var result = await agent.ExecAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", WindowsFolder + @"\setup-shares.ps1"],
            null, TimeSpan.FromMinutes(4), ct).ConfigureAwait(false);
        if (result is null) return Failed(shares, $"Windows' guest agent would not run the setup ({agent.LastError ?? "no answer"}).");
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var outcome = lines.LastOrDefault(l => l.StartsWith("result=", StringComparison.Ordinal))?[7..] ?? "";
        var why = outcome switch
        {
            "ok" => null,
            "no-virtiofs" => "Windows needs the guest tools for shared folders: put the VirtIO driver disc in (Settings, Guest tools) and power on again.",
            "no-device" => "Windows does not see the shared folder device; power the machine off and on (a restart inside Windows keeps the old hardware).",
            "no-driver" => "The virtio-fs driver is not installed: run the guest tools from the VirtIO driver disc, then power on again.",
            "no-winfsp" => "WinFsp could not be installed in Windows (it was not downloaded, or its installer failed).",
            _ => $"The setup did not finish ({(result.TimedOut ? "it took more than 4 minutes" : (result.Errors + result.Output).Trim())}).",
        };
        if (why is not null) return Failed(shares, why);
        var states = shares.Select(s =>
        {
            var drive = lines.FirstOrDefault(l => l.StartsWith($"drive {SafeTag(s.Tag)} ", StringComparison.Ordinal))?.Split(' ')[2];
            return new ShareState(s.Tag, DisplayName(s), drive is null ? null : $"{drive} \"{DisplayName(s)}\"", drive is null ? "the drive did not appear" : null);
        }).ToList();
        var good = states.Count(x => x.Where is not null);
        return new SharesReport(DateTimeOffset.Now,
            good == states.Count ? "In This PC as drives named after the folders, mounted again at every start." : $"{good} of {states.Count} shared folders are in Windows.", true, states);
    }

    /// <summary>The WinFsp installer, downloaded once and kept when its checksum matches; <c>null</c> when it cannot be had.</summary>
    public static async Task<string?> WinFspAsync(QontrolPaths paths, CancellationToken ct)
    {
        var target = Path.Combine(paths.Data, "guest-tools", Path.GetFileName(new Uri(WinFspUrl).AbsolutePath));
        if (File.Exists(target)) return target;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var bytes = await http.GetByteArrayAsync(WinFspUrl, ct).ConfigureAwait(false);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(WinFspSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("The WinFsp download did not match its checksum; it was not used.");
                return null;
            }

            await File.WriteAllBytesAsync(target + ".part", bytes, ct).ConfigureAwait(false);
            File.Move(target + ".part", target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Log.Warn($"WinFsp could not be downloaded ({ex.Message}).");
            return null;
        }
    }
}
