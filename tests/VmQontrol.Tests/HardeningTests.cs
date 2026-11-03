using System.Diagnostics;
using System.Formats.Tar;
using System.Text.Json.Nodes;
using VmQontrol.Core;
using VmQontrol.Core.Import;
using VmQontrol.Core.Library;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// Phase 18 (2026-11-03): the three audits' findings, each proven with the input that used to do harm. Every test
/// carries a control: the same path with a normal input still works.
/// </summary>
public sealed class HardeningTests
{
    [Fact]
    public void A_broken_settings_file_is_kept_and_said_and_nulls_never_crash_the_start()
    {
        using var profile = new TestProfile("hard-settings");
        Directory.CreateDirectory(profile.Paths.Config);
        File.WriteAllText(profile.Paths.SettingsFile, """{ "libraryRoots": ["/x/lib"], "favorites": [""" );
        var loaded = AppSettings.Load(profile.Paths);
        Assert.NotNull(AppSettings.LoadProblem);
        Assert.Equal([profile.Paths.DefaultLibrary], loaded.LibraryRoots);
        var kept = Directory.GetFiles(profile.Paths.Config, "settings.json.bad-*");
        Assert.Single(kept);
        Assert.Contains("/x/lib", File.ReadAllText(kept[0]));

        // null where lists belong: the start goes on with empty ones (it threw NullReferenceException before).
        File.WriteAllText(profile.Paths.SettingsFile, """{ "libraryRoots": null, "groupOf": null, "favorites": null, "groups": [null, "Work"] }""");
        var nulls = AppSettings.Load(profile.Paths);
        Assert.Null(AppSettings.LoadProblem);
        Assert.Equal([profile.Paths.DefaultLibrary], nulls.LibraryRoots);
        Assert.Equal(["Work"], nulls.OrderedGroups());
        // Control: a good file loads as written.
        File.WriteAllText(profile.Paths.SettingsFile, """{ "libraryRoots": ["/x/lib"] }""");
        Assert.Equal(["/x/lib"], AppSettings.Load(profile.Paths).LibraryRoots);
    }

    [Fact]
    public void Saves_from_many_threads_lose_nothing_and_leave_no_temporary_file()
    {
        using var profile = new TestProfile("hard-save");
        var settings = AppSettings.Load(profile.Paths);
        Parallel.For(0, 40, i => (settings with { Theme = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }).Save(profile.Paths));
        Assert.NotNull(AppSettings.Load(profile.Paths).Theme);
        Assert.Null(AppSettings.LoadProblem);
        Assert.Empty(Directory.GetFiles(profile.Paths.Config, "*.new"));
    }

    [Fact]
    public void A_machine_file_with_null_sections_is_an_error_card_and_a_path_shaped_id_is_replaced()
    {
        using var profile = new TestProfile("hard-vmjson");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "M");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "vm.json");
        MachineStore.Save(new MachineConfig { Name = "M" }, file);
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["disks"] = null;
        File.WriteAllText(file, json.ToJsonString());
        var ex = Assert.Throws<InvalidDataException>(() => MachineStore.Load(file));
        Assert.Contains("Disks", ex.Message, StringComparison.Ordinal);
        Assert.Contains(LibraryScanner.Scan(profile.Paths.DefaultLibrary), e => e.Error is not null);

        json["disks"] = new JsonArray();
        json["id"] = "../../../tmp/x";
        File.WriteAllText(file, json.ToJsonString());
        var m = MachineStore.Load(file);
        Assert.Matches("^[0-9a-f]{32}$", m.Id);
        // Control: a normal id is kept.
        MachineStore.Save(m with { Id = "abcdef0123456789" }, file);
        Assert.Equal("abcdef0123456789", MachineStore.Load(file).Id);
    }

    [Fact]
    public void Commas_in_paths_never_reach_qemu_as_option_separators()
    {
        var m = new MachineConfig { Name = "Test", Disks = [new DiskConfig { File = "disk0.qcow2" }],
            Media = [new MediaConfig { Role = MediaRole.Installer, File = "/isos/Win11, x64.iso", Connect = Connect.Always }] };
        var args = QemuCommandBuilder.Build(m, new LaunchContext
        {
            Host = RecordedHost.Load(), MachineDirectory = "/vms/Ubuntu, test", Runtime = new RuntimePaths("/run/vmq/m"),
            FileExists = _ => false, FirmwareFormat = _ => "raw",
        }).Args;
        Assert.Contains(args, a => a.Contains("file=/vms/Ubuntu,, test/disk0.qcow2", StringComparison.Ordinal));
        Assert.Contains(args, a => a.Contains("file=/isos/Win11,, x64.iso", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains("Ubuntu, test", StringComparison.Ordinal) || a.Contains("Win11, x64", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("-oProxyCommand=touch /tmp/x", "user")]
    [InlineData("x -oProxyCommand=y", "user")]
    [InlineData("alice", "alice")]
    [InlineData("first.last-2", "first.last-2")]
    public void The_ssh_account_never_becomes_an_option(string account, string expected)
    {
        var g = new GuestConfig { Account = account };
        Assert.Equal($"ssh -p 22220 {expected}@localhost", g.SshCommand(22220));
    }

    [Fact]
    public void An_imported_descriptor_cannot_name_system_files_devices_or_links_to_them()
    {
        using var profile = new TestProfile("hard-foreign");
        Assert.NotNull(ForeignPaths.Problem("/etc/shadow"));
        Assert.NotNull(ForeignPaths.Problem("/etc/passwd.vmdk"));
        Assert.NotNull(ForeignPaths.Problem("/dev/sda.img"));
        var link = Path.Combine(profile.Root, "disk.vmdk");
        File.CreateSymbolicLink(link, "/etc/hostname");
        Assert.NotNull(ForeignPaths.Problem(link));
        // Control: a disk image in an ordinary folder (here, under /run/media when the dev drive is) is fine.
        var disk = Path.Combine(profile.Root, "real.vmdk");
        File.WriteAllText(disk, "x");
        Assert.Null(ForeignPaths.Problem(disk));
        Assert.NotNull(ForeignPaths.Problem(disk, disc: true));
    }

    [Fact]
    public async Task A_shared_tar_cannot_write_through_links_and_loses_its_host_access()
    {
        using var profile = new TestProfile("hard-tar");
        var lib = profile.Paths.DefaultLibrary;
        Directory.CreateDirectory(lib);
        var outside = Path.Combine(profile.Root, "outside");
        Directory.CreateDirectory(outside);

        // A link to a folder elsewhere, then a file "inside" it: refused, nothing written outside.
        var evil = Path.Combine(profile.Root, "evil.tar");
        await using (var stream = File.Create(evil))
        await using (var writer = new TarWriter(stream))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "M/"), TestContext.Current.CancellationToken);
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "M/escape") { LinkName = outside }, TestContext.Current.CancellationToken);
            var file = new PaxTarEntry(TarEntryType.RegularFile, "M/escape/planted") { DataStream = new MemoryStream("x"u8.ToArray()) };
            await writer.WriteEntryAsync(file, TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => MachineExport.UnpackAsync(evil, lib, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(outside, "planted")));

        // A machine that asks for extra QEMU arguments and a host folder: they are removed, and its notes say so.
        var m = new MachineConfig { Name = "Shared", ExtraArgs = ["-monitor", "stdio"], Shares = [new ShareConfig { Path = "/home", Tag = "h" }] };
        var (safe, removed) = MachineExport.WithoutHostAccess(m, Path.Combine(lib, "Shared"));
        Assert.Empty(safe.ExtraArgs);
        Assert.Empty(safe.Shares);
        Assert.Equal(2, removed.Count);
        // Control: a machine with nothing outside its folder keeps everything.
        Assert.Empty(MachineExport.WithoutHostAccess(new MachineConfig { Name = "Plain" }, Path.Combine(lib, "Plain")).Removed);
    }

    [Fact]
    public void An_exported_quickemu_conf_never_runs_what_a_name_holds()
    {
        using var profile = new TestProfile("hard-conf");
        var marker = Path.Combine(profile.Root, "ran");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "M");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = $"x$(touch {marker})", Disks = [new DiskConfig { File = $"d$(touch {marker}).qcow2" }],
            Input = new InputConfig { Layout = $"us'; touch {marker}; '" } };
        var conf = MachineExport.WriteQuickemuConf(m, dir);
        // quickemu reads the file as bash: source it the same way.
        using var bash = Process.Start(new ProcessStartInfo("bash") { ArgumentList = { "-c", $"source \"$0\" && printf %s \"$keyboard_layout\"", conf }, RedirectStandardOutput = true })!;
        var layout = bash.StandardOutput.ReadToEnd();
        bash.WaitForExit();
        Assert.Equal(0, bash.ExitCode);
        Assert.False(File.Exists(marker), "sourcing the .conf ran a command from the machine's settings");
        Assert.Equal(m.Input.Layout, layout); // control: the value arrives exactly as written
    }

    [Fact]
    public void What_the_guest_sends_is_read_by_type_and_never_throws()
    {
        Assert.Null(JsonNode.Parse("5").Str());
        Assert.Equal("eth0", JsonNode.Parse("\"eth0\"").Str());
        Assert.Null(JsonNode.Parse("\"x\"").Long());
        Assert.Equal(7, JsonNode.Parse("7").Long());
        Assert.Empty(GuestJson.Base64("not base64!"));
        Assert.Equal("hi"u8.ToArray(), GuestJson.Base64("aGk="));
    }

    [Fact]
    public void Interrupted_imports_and_downloads_are_cleared_after_a_week_and_never_sooner()
    {
        using var profile = new TestProfile("hard-sweep");
        var lib = profile.Paths.DefaultLibrary;
        var old = Path.Combine(lib, ".importing", "old");
        var recent = Path.Combine(lib, ".downloads", "recent");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(recent);
        File.WriteAllText(Path.Combine(old, "part"), "x");
        File.WriteAllText(Path.Combine(recent, "part"), "x");
        var then = DateTime.UtcNow.AddDays(-9);
        File.SetLastWriteTimeUtc(Path.Combine(old, "part"), then);
        Directory.SetLastWriteTimeUtc(old, then);
        var removed = StagingSweep.Sweep([lib]);
        Assert.Equal([old], removed);
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public async Task A_second_start_while_one_is_under_way_is_refused_and_clears_nothing()
    {
        using var profile = new TestProfile("hard-start");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "M");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "M", Disks = [new DiskConfig { File = "disk0.qcow2" }] };
        Directory.CreateDirectory(profile.Paths.Runtime);
        var runtime = profile.Paths.RuntimeFor(m.Id);
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "marker"), "first start's sockets");
        // A lock naming a process that is not this machine's QEMU: Find takes the folder for a stopped machine's.
        File.WriteAllText(Path.Combine(runtime, "lock.json"), $$"""{ "pid": 999999, "machineDirectory": "{{dir}}", "started": "2026-01-01T00:00:00Z" }""");
        await using (new FileStream(runtime + ".starting", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<StartRefusedException>(() => MachineRunner.StartAsync(m, dir, profile.Paths, RecordedHost.Load(), new StartOptions { Headless = true }, TestContext.Current.CancellationToken));
            Assert.Contains("being started already", ex.Message, StringComparison.Ordinal);
            Assert.Null(MachineRunner.Find(m, profile.Paths));
            Assert.True(File.Exists(Path.Combine(runtime, "marker")), "Find cleared a folder a start holds");
        }

        // Control: with no start under way, a stale folder is cleared.
        Assert.Null(MachineRunner.Find(m, profile.Paths));
        Assert.False(Directory.Exists(runtime));
    }

    [Fact]
    public async Task A_used_machine_whose_disk_is_gone_is_not_given_a_blank_one()
    {
        using var profile = new TestProfile("hard-disk0");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "M");
        Directory.CreateDirectory(Path.Combine(dir, "tpm"));
        var iso = Path.Combine(profile.Root, "installer.iso");
        VmQontrol.Core.Media.IsoWriter.Write(iso, "INSTALL", new Dictionary<string, byte[]> { ["readme.txt"] = "x"u8.ToArray() });
        var m = new MachineConfig { Name = "M", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
            Media = [new MediaConfig { Role = MediaRole.Installer, File = iso, Connect = Connect.UntilInstalled }] };
        var ex = await Assert.ThrowsAsync<StartRefusedException>(() => MachineRunner.StartAsync(m, dir, profile.Paths, RecordedHost.Load(), new StartOptions { Headless = true }, TestContext.Current.CancellationToken));
        Assert.Contains("is missing, and the machine has been used before", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir, "disk0.qcow2")));
    }

    [Fact]
    public void The_owner_check_reads_this_account_for_its_own_folder()
    {
        using var profile = new TestProfile("hard-owner");
        Directory.CreateDirectory(profile.Root);
        Assert.Equal(0, NativeLibc.Stat(profile.Root, out var owner));
        Assert.Equal(NativeLibc.Geteuid(), owner);
        Assert.NotEqual(0, NativeLibc.Stat("/nonexistent/x", out _));
    }
}
