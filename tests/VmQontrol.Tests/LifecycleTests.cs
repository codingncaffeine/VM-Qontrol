using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

[Trait("Tier", "Integration")]
public sealed class LifecycleTests
{
    [Fact]
    public async Task A_full_clone_has_identical_disks_its_own_identity_and_leaves_the_original_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("clone");
        var lib = profile.Paths.DefaultLibrary;
        var dir = Path.Combine(lib, "Original");
        Directory.CreateDirectory(dir);
        var disk = Path.Combine(dir, "disk0.qcow2");
        await QemuImg.CreateAsync(disk, "qcow2", 1, ct: ct);
        // Real data in the disk: a pattern qemu-io writes, which the copy must hold too.
        Assert.SkipWhen(!File.Exists("/usr/bin/qemu-io"), "qemu-io is not installed");
        await Run("qemu-io", "-c", "write -P 0xab 1M 2M", disk);
        File.WriteAllText(Path.Combine(dir, "OVMF_VARS.fd"), "vars");
        var original = new MachineConfig
        {
            Name = "Original",
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
            Media = [new MediaConfig { Role = MediaRole.Unattended, File = "unattended.iso" }],
            Nics = [new NicConfig { Mac = "52:54:00:aa:bb:cc" }],
        };
        MachineStore.Save(original, Path.Combine(dir, "vm.json"));

        var (clone, cloneDir) = await MachineLifecycle.CloneAsync(original, dir, lib, "Copy", ct: ct);

        Assert.Equal(Path.Combine(lib, "Copy"), cloneDir);
        Assert.NotEqual(original.Id, clone.Id);
        Assert.NotEqual("52:54:00:aa:bb:cc", clone.Nics[0].Mac);
        Assert.StartsWith("52:54:00:", clone.Nics[0].Mac, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(dir, "unattended.iso"), clone.Media[0].File);
        // The control: qemu-img itself finds the two disks' contents identical.
        await QemuImg.RunAsync(ct, "compare", disk, Path.Combine(cloneDir, "disk0.qcow2"));
        Assert.Contains("read 2097152/2097152 bytes", await Run("qemu-io", "-c", "read -P 0xab 1M 2M", Path.Combine(cloneDir, "disk0.qcow2")), StringComparison.Ordinal);
        Assert.Equal("vars", File.ReadAllText(Path.Combine(cloneDir, "OVMF_VARS.fd")));
        Assert.Equal(MachineStore.ToJson(original), MachineStore.ToJson(MachineStore.Load(Path.Combine(dir, "vm.json"))));

        MachineLifecycle.Delete(cloneDir, null);
        Assert.False(Directory.Exists(cloneDir));
        Assert.True(File.Exists(disk));
    }

    [Fact]
    public async Task A_linked_clone_shares_a_frozen_base_and_neither_machine_sees_the_others_writes()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!File.Exists("/usr/bin/qemu-io"), "qemu-io is not installed");
        using var profile = new TestProfile("linked");
        var lib = profile.Paths.DefaultLibrary;
        var (original, dir) = await Machine(lib, "Original", ct);
        var disk = Path.Combine(dir, "disk0.qcow2");

        var (clone, cloneDir, updated) = await MachineLifecycle.LinkedCloneAsync(original, dir, lib, "Linked", ct: ct);

        // The original's disk is now a layer on a read-only base in the library's bases folder; so is the clone's.
        var baseFile = DiskState.BackingFile(disk);
        Assert.NotNull(baseFile);
        Assert.Equal(Path.Combine(lib, MachineLifecycle.BasesFolder), Path.GetDirectoryName(baseFile));
        Assert.Equal(baseFile, DiskState.BackingFile(Path.Combine(cloneDir, "disk0.qcow2")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(baseFile!));
        Assert.True(DiskState.IsUsed(disk));
        Assert.True(DiskState.IsUsed(Path.Combine(cloneDir, "disk0.qcow2")));
        Assert.NotEqual(original.Id, clone.Id);
        Assert.Equal(MachineStore.ToJson(updated), MachineStore.ToJson(MachineStore.Load(Path.Combine(dir, "vm.json"))));

        // Both see the original's data; each one's writes stay its own.
        var cloneDisk = Path.Combine(cloneDir, "disk0.qcow2");
        Assert.Contains("read 2097152/2097152 bytes", await Run("qemu-io", "-c", "read -P 0xab 1M 2M", cloneDisk), StringComparison.Ordinal);
        await Run("qemu-io", "-c", "write -P 0xcd 1M 1M", cloneDisk);
        await Run("qemu-io", "-c", "write -P 0xef 2M 1M", disk);
        Assert.Contains("read 1048576/1048576 bytes", await Run("qemu-io", "-c", "read -P 0xab 1M 1M", disk), StringComparison.Ordinal);
        Assert.Contains("read 1048576/1048576 bytes", await Run("qemu-io", "-c", "read -P 0xab 2M 1M", cloneDisk), StringComparison.Ordinal);

        // A linked clone of the linked clone: its frozen layer keeps pointing at the first base from the bases folder.
        var (_, secondDir, _) = await MachineLifecycle.LinkedCloneAsync(clone, cloneDir, lib, "Second", ct: ct);
        var secondDisk = Path.Combine(secondDir, "disk0.qcow2");
        Assert.Equal(3, DiskState.Chain(secondDisk).Count);
        Assert.Contains("read 1048576/1048576 bytes", await Run("qemu-io", "-c", "read -P 0xcd 1M 1M", secondDisk), StringComparison.Ordinal);
        Assert.Contains("read 1048576/1048576 bytes", await Run("qemu-io", "-c", "read -P 0xab 2M 1M", secondDisk), StringComparison.Ordinal);

        // Bases go only when nothing is layered on them.
        Assert.Empty(MachineLifecycle.BasesFreedBy(dir, [lib]));
        MachineLifecycle.Delete(dir, null);
        Assert.Empty(MachineLifecycle.PruneBases([lib]));
        // "Linked" was frozen for "Second", so the two share both bases: neither one's deletion frees any.
        Assert.Empty(MachineLifecycle.BasesFreedBy(secondDir, [lib]));
        MachineLifecycle.Delete(secondDir, null);
        Assert.Empty(MachineLifecycle.PruneBases([lib]));
        Assert.Contains("read 1048576/1048576 bytes", await Run("qemu-io", "-c", "read -P 0xcd 1M 1M", cloneDisk), StringComparison.Ordinal);
        Assert.Equal(2, MachineLifecycle.BasesFreedBy(cloneDir, [lib]).Count);
        MachineLifecycle.Delete(cloneDir, null);
        Assert.Equal(2, MachineLifecycle.PruneBases([lib]).Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(lib, MachineLifecycle.BasesFolder)));
    }

    [Fact]
    public async Task A_linked_clone_of_a_machine_with_snapshots_copies_its_state_and_keeps_its_snapshots()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!File.Exists("/usr/bin/qemu-io"), "qemu-io is not installed");
        using var profile = new TestProfile("linked-snap");
        var lib = profile.Paths.DefaultLibrary;
        var (original, dir) = await Machine(lib, "Original", ct);
        var disk = Path.Combine(dir, "disk0.qcow2");
        await QemuImg.RunAsync(ct, "snapshot", "-c", "before", disk);
        await Run("qemu-io", "-c", "write -P 0x11 1M 1M", disk);

        var (_, cloneDir, updated) = await MachineLifecycle.LinkedCloneAsync(original, dir, lib, "Linked", ct: ct);

        Assert.Null(DiskState.BackingFile(disk));
        Assert.Same(original, updated);
        Assert.Contains("before", await QemuImg.RunAsync(ct, "snapshot", "-l", disk), StringComparison.Ordinal);
        var cloneDisk = Path.Combine(cloneDir, "disk0.qcow2");
        Assert.Contains("read 1048576/1048576 bytes", await Run("qemu-io", "-c", "read -P 0x11 1M 1M", cloneDisk), StringComparison.Ordinal);
        Assert.Contains("read 1048576/1048576 bytes", await Run("qemu-io", "-c", "read -P 0xab 2M 1M", cloneDisk), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linking_a_machine_twice_from_a_deeper_folder_keeps_every_layer_readable()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!File.Exists("/usr/bin/qemu-io"), "qemu-io is not installed");
        using var profile = new TestProfile("linked-deep");
        var lib = profile.Paths.DefaultLibrary;
        var (original, dir) = await Machine(Path.Combine(lib, "Group"), "Original", ct);
        var (_, _, once) = await MachineLifecycle.LinkedCloneAsync(original, dir, lib, "First", ct: ct);
        await Run("qemu-io", "-c", "write -P 0x22 1M 1M", Path.Combine(dir, "disk0.qcow2"));

        // The second freeze moves a layer whose base is named relative to a folder two levels down.
        var (_, secondDir, _) = await MachineLifecycle.LinkedCloneAsync(once, dir, lib, "Second", ct: ct);

        var chain = DiskState.Chain(Path.Combine(secondDir, "disk0.qcow2"));
        Assert.Equal(3, chain.Count);
        Assert.All(chain, f => Assert.True(File.Exists(f), f));
        Assert.Contains("read 1048576/1048576 bytes", await Run("qemu-io", "-c", "read -P 0x22 1M 1M", chain[0]), StringComparison.Ordinal);
        Assert.Contains("read 1048576/1048576 bytes", await Run("qemu-io", "-c", "read -P 0xab 2M 1M", chain[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_base_another_library_root_still_uses_is_never_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!File.Exists("/usr/bin/qemu-io"), "qemu-io is not installed");
        using var profile = new TestProfile("linked-roots");
        var rootA = profile.Paths.DefaultLibrary;
        var rootB = Path.Combine(Path.GetDirectoryName(rootA)!, "OtherRoot");
        var (original, dir) = await Machine(rootB, "Original", ct);
        var (_, cloneDir, _) = await MachineLifecycle.LinkedCloneAsync(original, dir, rootA, "Linked", ct: ct);

        Assert.Empty(MachineLifecycle.BasesFreedBy(cloneDir, [rootA, rootB]));
        MachineLifecycle.Delete(cloneDir, null);
        Assert.Empty(MachineLifecycle.PruneBases([rootA, rootB]));
        Assert.Contains("read 2097152/2097152 bytes", await Run("qemu-io", "-c", "read -P 0xab 1M 2M", Path.Combine(dir, "disk0.qcow2")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_template_is_never_started_and_its_machines_are_ordinary_machines()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!File.Exists("/usr/bin/qemu-io"), "qemu-io is not installed");
        using var profile = new TestProfile("template");
        var lib = profile.Paths.DefaultLibrary;
        var (original, dir) = await Machine(lib, "Base", ct);

        var template = MachineLifecycle.SetTemplate(original, dir, true);

        Assert.True(MachineStore.Load(Path.Combine(dir, "vm.json")).IsTemplate);
        var refused = await Assert.ThrowsAsync<StartRefusedException>(() => MachineRunner.StartAsync(template, dir, profile.Paths, VmQontrol.Core.Hosts.HostProbe.Probe(), new StartOptions { Headless = true }, ct));
        Assert.Contains("template", refused.Message, StringComparison.Ordinal);
        var (made, madeDir, _) = await MachineLifecycle.LinkedCloneAsync(template, dir, lib, "Made", ct: ct);
        Assert.False(made.IsTemplate);
        Assert.False(MachineStore.Load(Path.Combine(madeDir, "vm.json")).IsTemplate);
        Assert.StartsWith("Made from the template Base", made.Notes, StringComparison.Ordinal);
        Assert.Contains("read 2097152/2097152 bytes", await Run("qemu-io", "-c", "read -P 0xab 1M 2M", Path.Combine(madeDir, "disk0.qcow2")), StringComparison.Ordinal);

        // A suspended machine or a quickemu machine is not made a template.
        File.WriteAllText(MachineSuspend.StatePath(madeDir), "state");
        Assert.Throws<InvalidOperationException>(() => MachineLifecycle.SetTemplate(made, madeDir, true));
        Assert.Throws<InvalidOperationException>(() => MachineLifecycle.SetTemplate(made with { ImportedFrom = new ImportSource("quickemu", "/x.conf") }, madeDir, true));
        Assert.False(MachineStore.Load(Path.Combine(madeDir, "vm.json")).IsTemplate);
        Assert.False(MachineLifecycle.SetTemplate(template, dir, false).IsTemplate);
    }

    private static async Task<(MachineConfig Machine, string Directory)> Machine(string lib, string name, CancellationToken ct)
    {
        var dir = Path.Combine(lib, name);
        Directory.CreateDirectory(dir);
        var disk = Path.Combine(dir, "disk0.qcow2");
        await QemuImg.CreateAsync(disk, "qcow2", 1, ct: ct);
        await Run("qemu-io", "-c", "write -P 0xab 1M 2M", disk);
        var m = new MachineConfig { Name = name, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }], Nics = [new NicConfig { Mac = "52:54:00:aa:bb:cc" }] };
        MachineStore.Save(m, Path.Combine(dir, "vm.json"));
        return (m, dir);
    }

    private static async Task<string> Run(string program, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(start)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        return output;
    }
}
