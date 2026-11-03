using System.Diagnostics;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>A disk another process holds: the refusal says who (s9: a test copy's layers held a user's OpenCore.qcow2).</summary>
public sealed class FileHoldersTests
{
    [Fact]
    public void The_locked_file_is_read_from_QEMUs_message()
    {
        const string Message = "qemu-system-x86_64: -drive id=BootLoader,if=none,format=qcow2,file=/vms/m/OpenCore.qcow2: Failed to get \"write\" lock\nIs another process using the image [/vms/m/OpenCore.qcow2]?";
        Assert.Equal("/vms/m/OpenCore.qcow2", FileHolders.LockedFile(Message));
        Assert.Null(FileHolders.LockedFile("qemu-system-x86_64: -device foo: no such device"));
        Assert.Null(FileHolders.Explain("something else"));
    }

    [Trait("Tier", "Integration")]
    [Fact]
    public async Task Powering_on_a_machine_whose_disk_another_machine_holds_names_that_machine()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("holders");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Held");
        Directory.CreateDirectory(dir);
        var disk = Path.Combine(dir, "disk0.qcow2");
        await QemuImg.CreateAsync(disk, "qcow2", 1, ct: ct);
        var m = new MachineConfig { Name = "Held", MemoryMiB = 256, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios }, Audio = new AudioConfig { Device = "none" }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
        var start = new ProcessStartInfo("qemu-system-x86_64") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in (string[])["-name", "holder,process=holder", "-machine", "q35,accel=kvm", "-m", "128", "-display", "none", "-audiodev", "none,id=a", "-net", "none", "-drive", $"file={disk},format=qcow2,if=virtio"])
            start.ArgumentList.Add(a);
        using var holder = Process.Start(start)!;
        try
        {
            await Task.Delay(1500, ct);
            Assert.False(holder.HasExited, "the holding QEMU did not start");
            var ex = await Assert.ThrowsAsync<StartRefusedException>(() => MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct));
            Assert.Equal($"disk0.qcow2 is in use by the virtual machine \"holder\" (process {holder.Id}). Close that, then power on again.", ex.Reasons[0]);
        }
        finally
        {
            holder.Kill();
            await holder.WaitForExitAsync(CancellationToken.None);
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }
    }
}
