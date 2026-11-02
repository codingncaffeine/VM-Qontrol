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
