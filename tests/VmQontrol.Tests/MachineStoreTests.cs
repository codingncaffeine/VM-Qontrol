using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

public sealed class MachineStoreTests
{
    [Fact]
    public void A_machine_round_trips_through_vm_json()
    {
        using var profile = new TestProfile("store");
        var file = Path.Combine(profile.Root, MachineStore.FileName);
        var machine = new MachineConfig
        {
            Name = "Dev box",
            Guest = new GuestConfig { Os = "windows", Release = "11" },
            MemoryMiB = 8192,
            Tpm = true,
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 80 }],
            Media = [new MediaConfig { Role = MediaRole.Installer, File = "/isos/win11.iso", Connect = Connect.UntilInstalled }],
            Nics = [new NicConfig { Forwards = [new PortForward("tcp", 3389, 3389)] }],
            Usb = new UsbConfig { Passthrough = [new UsbDevice("046d", "c52b", "Logitech receiver")] },
        };
        MachineStore.Save(machine, file);
        var back = MachineStore.Load(file);
        Assert.Equal(MachineStore.ToJson(machine), MachineStore.ToJson(back));
        Assert.False(File.Exists(file + ".new"));
        Assert.Contains("\"installer\"", File.ReadAllText(file).ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_format_is_refused_with_a_reason()
    {
        using var profile = new TestProfile("store");
        var file = Path.Combine(profile.Root, MachineStore.FileName);
        File.WriteAllText(file, """{"schemaVersion": 99, "name": "x"}""");
        var ex = Assert.Throws<InvalidDataException>(() => MachineStore.Load(file));
        Assert.Contains("newer VM Qontrol", ex.Message, StringComparison.Ordinal);
    }
}
