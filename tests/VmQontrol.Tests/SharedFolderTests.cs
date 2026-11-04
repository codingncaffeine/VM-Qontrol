using System.Diagnostics;
using VmQontrol.Core.Downloads;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>D42: shared folders that appear in the machine by themselves.</summary>
public sealed class SharedFolderTests
{
    [Fact]
    public void Tags_are_safe_mount_names_of_at_most_36_bytes()
    {
        Assert.Equal("my-projects", SharedFolders.SafeTag("My Projects!"));
        Assert.Equal("share", SharedFolders.SafeTag("  ***  "));
        Assert.Equal(36, SharedFolders.SafeTag(new string('a', 50)).Length);
        Assert.Equal("a-b", SharedFolders.SafeTag("a / b"));
    }

    [Fact]
    public void Qemu_gets_the_same_tag_the_guest_mounts()
    {
        var m = new MachineConfig { Name = "Web", Disks = [new DiskConfig { File = "disk0.qcow2" }], Shares = [new ShareConfig { Path = "/home/ada/My Projects", Tag = "My Projects" }] };
        var launch = VmQontrol.Core.Qemu.QemuCommandBuilder.Build(m, new VmQontrol.Core.Qemu.LaunchContext
        {
            Host = RecordedHost.Load(), MachineDirectory = "/vms/web", Runtime = new VmQontrol.Core.Qemu.RuntimePaths("/run/vmq/web"), FileExists = _ => false,
            FirmwareFormat = _ => "raw", Virtiofsd = "/usr/lib/virtiofsd", VirtiofsdTranslates = true, HostIds = (1234, 99), ShareOwner = 1001,
        });
        // Every shared file belongs to the guest's first user; what the guest makes is this account's (VMware's behavior).
        Assert.Contains("--translate-uid=host:1234:1001:1", launch.Shares[0].Args);
        Assert.Contains("--translate-uid=squash-guest:0:1234:4294967295", launch.Shares[0].Args);
        Assert.Contains("--translate-gid=host:99:1001:1", launch.Shares[0].Args);
        Assert.Equal(["--uid-map=:1000:1234:1:", "--gid-map=:1000:99:1:"], SharedFolders.OwnerArgs(false, (1234, 99)));
        Assert.Contains("vhost-user-fs-pci,chardev=fs0,tag=my-projects", string.Join(' ', launch.Args), StringComparison.Ordinal);
        Assert.Contains("my-projects /mnt/shared/my-projects virtiofs defaults,nofail$X 0 0 # vmqontrol", SharedFolders.LinuxScript(m.Shares), StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_setup_names_each_drive_after_its_folder_and_quotes_names()
    {
        var script = SharedFolders.WindowsScript([new ShareConfig { Path = "/home/ada/Ada's Work", Tag = "work" }, new ShareConfig { Path = "/data/Music", Tag = "music" }]);
        Assert.Contains("$tags = @('work','music')", script, StringComparison.Ordinal);
        Assert.Contains("$names = @('Ada''s Work','Music')", script, StringComparison.Ordinal);
        Assert.Contains("CommandLine '-t %1 -m %2 -F NTFS'", script, StringComparison.Ordinal);
        Assert.Contains("Register-ScheduledTask -TaskName 'VM Qontrol shared folders'", script, StringComparison.Ordinal);
    }

    /// <summary>The Windows setup parses as PowerShell (PowerShell's own parser, from scratch/tools/pwsh: dotnet tool install PowerShell --tool-path scratch/tools/pwsh).</summary>
    [Fact]
    public async Task The_windows_setup_is_valid_powershell()
    {
        var pwsh = Path.Combine(TestProfile.ScratchRoot, "tools", "pwsh", "pwsh");
        Assert.SkipWhen(!File.Exists(pwsh), "PowerShell is not in scratch/tools/pwsh");
        using var profile = new TestProfile("shares-ps");
        var file = Path.Combine(profile.Root, "setup-shares.ps1");
        await File.WriteAllTextAsync(file, SharedFolders.WindowsScript([new ShareConfig { Path = "/home/ada/Ada's Work", Tag = "work" }]), TestContext.Current.CancellationToken);
        var start = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     "$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($env:VMQ_PS1, [ref]$null, [ref]$e); $e | ForEach-Object { $_.ToString() }; exit $e.Count" })
            start.ArgumentList.Add(a);
        start.Environment["VMQ_PS1"] = file;
        using var p = Process.Start(start)!;
        var output = await p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken) + await p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(p.ExitCode == 0, output);

        // The mount script the setup writes at run time: built by PowerShell itself, three lines, and it parses too
        // (a comma binding tighter than + once glued it into one line that never ran).
        var setup = SharedFolders.WindowsScript([new ShareConfig { Path = "/home/ada/Work", Tag = "work" }]);
        var from = setup.IndexOf("$mount = @(", StringComparison.Ordinal);
        var to = setup.IndexOf("# end of the mount script", StringComparison.Ordinal);
        var built = Path.Combine(profile.Root, "mount-shares.ps1");
        var builder = Path.Combine(profile.Root, "build-mount.ps1");
        await File.WriteAllTextAsync(builder, "$launchctl = 'C:\\W\\launchctl-x64.exe'; $dir = 'C:\\ProgramData\\VMQontrol'\n" + setup[from..to]
            + "\nSet-Content $env:VMQ_OUT $mount\n", TestContext.Current.CancellationToken);
        var run = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", builder }) run.ArgumentList.Add(a);
        run.Environment["VMQ_OUT"] = built;
        using (var b = Process.Start(run)!)
        {
            var said = await b.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken) + await b.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await b.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(b.ExitCode == 0, said);
        }

        var lines = (await File.ReadAllLinesAsync(built, TestContext.Current.CancellationToken)).Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("$launchctl = 'C:\\W\\launchctl-x64.exe'", lines[0], StringComparison.Ordinal);
        start.Environment["VMQ_PS1"] = built;
        using var q = Process.Start(start)!;
        var parsed = await q.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken) + await q.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await q.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(q.ExitCode == 0, parsed);
    }

    public static TheoryData<string> Images => [.. CloudImages.All.Select(i => i.Id)];

    /// <summary>
    /// The whole path on a real guest, set VMQONTROL_SHARE_PROOF="alpine debian-13 …" (or "all"): a cloud machine with a
    /// writable and a read-only folder, the connector run as the app runs it, then over SSH: both folders mounted, a file
    /// from this computer read in the guest, a file from the guest here, the read-only one refusing a write; after a
    /// reboot inside the guest the folders are back with nothing from this side (fstab), and a second run changes nothing.
    /// </summary>
    [Trait("Tier", "Guest")]
    [Theory]
    [MemberData(nameof(Images))]
    public async Task Shared_folders_appear_in_a_linux_guest_by_themselves_and_after_a_reboot(string id)
    {
        var wanted = Environment.GetEnvironmentVariable("VMQONTROL_SHARE_PROOF") ?? "";
        Assert.SkipWhen(wanted != "all" && !wanted.Split(' ').Contains(id), "set VMQONTROL_SHARE_PROOF to run it (boots a cloud image)");
        var ct = TestContext.Current.CancellationToken;
        var image = CloudImages.All.Single(i => i.Id == id);
        using var profile = new TestProfile("shares-" + id);
        CloudImages.CacheOverride = Path.Combine(TestProfile.ScratchRoot, "images", "cloud");
        var key = Path.Combine(profile.Root, "key");
        using (var gen = Process.Start("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", key])!) await gen.WaitForExitAsync(ct);
        var work = Path.Combine(profile.Root, "Work Folder");
        var music = Path.Combine(profile.Root, "Music");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(music);
        await File.WriteAllTextAsync(Path.Combine(work, "from-host.txt"), "hello from the host\n", ct);
        await File.WriteAllTextAsync(Path.Combine(music, "song.txt"), "la\n", ct);

        var resolved = await CloudImages.ResolveAsync(image, ct);
        var file = await CloudImages.GetAsync(profile.Paths, resolved, new Progress<DownloadProgress>(), ct);
        var (m, dir) = await CloudImages.CreateAsync(profile.Paths.DefaultLibrary, image, file, new CloudChoices
        {
            Name = "shares " + id, UserName = "Ada", SshKeys = [File.ReadAllText(key + ".pub").Trim()], MemoryMiB = 1536,
        }, ct);
        m = m with { Shares = [new ShareConfig { Path = work, Tag = "Work Folder" }, new ShareConfig { Path = music, Tag = "music", ReadOnly = true }] };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        var log = new System.Text.StringBuilder();
        try
        {
            async Task<string> Ssh(string command, TimeSpan within)
            {
                var deadline = DateTime.UtcNow + within;
                string output = "";
                while (true)
                {
                    Assert.True(DateTime.UtcNow < deadline, $"no SSH answer in time; last: {output}");
                    var ssh = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
                    foreach (var a in new[] { "-F", "/dev/null", "-i", key, "-p", running.SshPort!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                 "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", "ada@localhost", command + "; echo end-of-proof" })
                        ssh.ArgumentList.Add(a);
                    using var p = Process.Start(ssh)!;
                    output = (await p.StandardOutput.ReadToEndAsync(ct)) + (await p.StandardError.ReadToEndAsync(ct));
                    await p.WaitForExitAsync(ct);
                    if (output.Contains("end-of-proof", StringComparison.Ordinal)) return output;
                    await Task.Delay(5000, ct);
                }
            }

            // The connector as the helper runs it: waits for the agent (cloud-init installs it), then sets up.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(7));
            var watch = Stopwatch.StartNew();
            var report = await SharedFolders.WatchAsync(running, profile.Paths, cts.Token);
            log.AppendLine($"{id}: connector after {watch.Elapsed.TotalSeconds:0} s: {report?.Summary}");
            log.AppendLine(string.Join("\n", report?.Shares.Select(x => $"  {x.Tag}: {x.Where ?? x.Problem}") ?? []));
            log.AppendLine("diagnostics:\n" + await Ssh("getenforce 2>/dev/null; sudo -n ausearch -m avc -ts recent 2>/dev/null | grep -o 'avc: .*' | tail -8; command -v mount; ls -laZ /usr/bin/mount 2>/dev/null; sudo -n cat /etc/sysconfig/qemu-ga 2>/dev/null | grep -v '^#'", TimeSpan.FromMinutes(3)));
            if (report?.Owner is { } learned && learned != SharedFolders.GuestOwner)
            {
                // The machine's account is not user 1000 (Alpine's image has its own "alpine" user): the files are its
                // own from one power off and on, with nothing typed.
                Assert.Contains("next power off and on", report.Summary, StringComparison.Ordinal);
                Assert.Equal(learned, SharedFolders.ReadOwner(dir));
                await MachineRunner.PowerOffAsync(running, ct);
                await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(30), ct);
                running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
                report = await SharedFolders.WatchAsync(running, profile.Paths, cts.Token);
                log.AppendLine($"{id}: after one power off and on (owner {learned}): {report?.Summary}");
                Assert.DoesNotContain("next power off", report?.Summary ?? "", StringComparison.Ordinal);
            }

            Assert.NotNull(report);
            Assert.True(report.Done, report.Summary);
            Assert.All(report.Shares, s => Assert.Equal(s.Tag == "music" ? "/mnt/shared/music" : "/mnt/shared/work-folder", s.Where));
            Assert.Equal(report.Summary, SharedFolders.ReadReport(running.Paths)?.Summary);

            const string Check = "cat /mnt/shared/work-folder/from-host.txt; echo guest-wrote > /mnt/shared/work-folder/from-guest.txt && echo wrote-ok; sudo -n sh -c 'echo root > /mnt/shared/work-folder/from-root.txt' && echo root-ok; [ \"$(stat -c %u /mnt/shared/work-folder/from-host.txt)\" = \"$(id -u)\" ] && echo owner-is-me; "
                                 + "cat /mnt/shared/music/song.txt; (echo x > /mnt/shared/music/nope.txt) 2>/dev/null && echo ro-broken || echo ro-refused; grep -c '# vmqontrol' /etc/fstab; id; grep -E ':(0|100[0-9]):' /etc/passwd; ls -lan /mnt/shared/work-folder";
            var first = await Ssh(Check, TimeSpan.FromMinutes(3));
            log.AppendLine("first:\n" + first);
            Assert.Contains("hello from the host", first, StringComparison.Ordinal);
            Assert.Contains("wrote-ok", first, StringComparison.Ordinal);
            Assert.Contains("root-ok", first, StringComparison.Ordinal);
            Assert.Contains("owner-is-me", first, StringComparison.Ordinal); // the files are the machine's account's
            File.Delete(Path.Combine(work, "from-root.txt")); // made by the guest's root, yet this account's to delete
            Assert.Contains("la", first, StringComparison.Ordinal);
            Assert.Contains("ro-refused", first, StringComparison.Ordinal);
            Assert.Equal("guest-wrote\n", await File.ReadAllTextAsync(Path.Combine(work, "from-guest.txt"), ct));
            Assert.False(File.Exists(Path.Combine(music, "nope.txt")));

            // A second run (the next power on) leaves fstab as it is.
            var again = await SharedFolders.ConnectAsync(new GuestAgentClient(running.Paths.Agent), m.Shares, profile.Paths, ct);
            Assert.True(again.Shares.All(s => s.Where is not null), again.Summary);

            // A restart inside the guest: the folders come back with nothing from this side.
            File.Delete(Path.Combine(work, "from-guest.txt"));
            var bootBefore = (await Ssh("cat /proc/sys/kernel/random/boot_id", TimeSpan.FromMinutes(1))).Split('\n')[0].Trim();
            await Ssh("(sudo -n reboot >/dev/null 2>&1 &)", TimeSpan.FromMinutes(1));
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            string after;
            var rebootDeadline = DateTime.UtcNow.AddMinutes(4);
            do
            {
                Assert.True(DateTime.UtcNow < rebootDeadline, "the guest did not restart");
                after = await Ssh("cat /proc/sys/kernel/random/boot_id; grep virtiofs /proc/mounts; cat /mnt/shared/work-folder/from-host.txt; grep -c '# vmqontrol' /etc/fstab; ls /mnt/shared/work-folder", TimeSpan.FromMinutes(4));
                if (after.Split('\n')[0].Trim() == bootBefore) await Task.Delay(5000, ct);
            }
            while (after.Split('\n')[0].Trim() == bootBefore);
            log.AppendLine("after reboot:\n" + after);
            Assert.NotEqual(bootBefore, after.Split('\n')[0].Trim()); // the guest did restart
            Assert.Contains("work-folder /mnt/shared/work-folder virtiofs", after, StringComparison.Ordinal);
            // Fedora's SELinux keeps the agent from writing fstab: the guest's own mounter (cloud-init) mounts the folders,
            // the read-only one read-write in the guest while this computer refuses every write to it.
            var agentWrites = id != "fedora";
            Assert.Contains(agentWrites ? "music /mnt/shared/music virtiofs ro" : "music /mnt/shared/music virtiofs", after, StringComparison.Ordinal);
            Assert.Contains("hello from the host", after, StringComparison.Ordinal);
            Assert.Contains(agentWrites ? "\n2\n" : "\n0\n", after, StringComparison.Ordinal); // two lines in fstab, not four
            Assert.DoesNotContain("from-guest.txt", after, StringComparison.Ordinal); // the host's delete is seen
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(TestProfile.ScratchRoot, $"shares-proof-{id}.txt"), log.ToString(), CancellationToken.None);
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
            CloudImages.CacheOverride = null;
        }
    }
}

/// <summary>D42: vmq share / unshare (vmrun addSharedFolder / removeSharedFolder).</summary>
public sealed class CliShareTests
{
    private static (int Code, string Out, string Err) Vmq(TestProfile profile, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = VmQontrol.Cli.Commands.Run(args, output, error, profile.Paths, RecordedHost.Load);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void A_folder_is_shared_and_unshared_from_the_command_line()
    {
        using var profile = new TestProfile("cli-share");
        var folder = Path.Combine(profile.Root, "My Projects");
        Directory.CreateDirectory(folder);
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Web");
        Directory.CreateDirectory(dir);
        var vmJson = Path.Combine(dir, MachineStore.FileName);
        MachineStore.Save(new MachineConfig { Name = "Web", Disks = [new DiskConfig { File = "disk0.qcow2" }] }, vmJson);

        var (code, output, error) = Vmq(profile, "share", "Web", folder, "--read-only");
        Assert.True(code == 0, error);
        Assert.Contains("/mnt/shared/my-projects", output, StringComparison.Ordinal);
        Assert.Equal([new ShareConfig { Path = folder, Tag = "my-projects", ReadOnly = true }], MachineStore.Load(vmJson).Shares);

        Assert.Equal(1, Vmq(profile, "share", "Web", folder).Code); // once is enough
        Assert.Equal(1, Vmq(profile, "share", "Web", Path.Combine(profile.Root, "missing")).Code);
        Assert.Equal(1, Vmq(profile, "unshare", "Web", "other").Code);

        (code, _, error) = Vmq(profile, "unshare", "Web", "my-projects");
        Assert.True(code == 0, error);
        Assert.Empty(MachineStore.Load(vmJson).Shares);
    }
}
