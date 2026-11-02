using System.Text.Json.Nodes;
using VmQontrol.Core.Devices;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

public sealed class UsbTests
{
    private static void Device(string root, string port, Dictionary<string, string> files)
    {
        var dir = Path.Combine(root, port);
        Directory.CreateDirectory(dir);
        foreach (var (name, value) in files) File.WriteAllText(Path.Combine(dir, name), value + "\n");
    }

    [Fact]
    public void Devices_are_listed_by_name_without_hubs_or_interfaces()
    {
        using var profile = new TestProfile("usb");
        var sys = Path.Combine(profile.Root, "sys");
        Device(sys, "3-2", new() { ["idVendor"] = "046d", ["idProduct"] = "c52b", ["manufacturer"] = "Logitech", ["product"] = "USB Receiver", ["busnum"] = "3", ["devnum"] = "4", ["speed"] = "12", ["bDeviceClass"] = "00" });
        Device(sys, "3-1", new() { ["idVendor"] = "05e3", ["idProduct"] = "0610", ["busnum"] = "3", ["devnum"] = "2", ["speed"] = "480", ["bDeviceClass"] = "09" });
        Device(sys, "3-2:1.0", new() { ["bInterfaceClass"] = "03" });
        Device(sys, "usb3", new() { ["idVendor"] = "1d6b", ["idProduct"] = "0002" });
        var list = UsbHost.List(sys, _ => false);
        var d = Assert.Single(list);
        Assert.Equal("Logitech USB Receiver", d.Name);
        Assert.Equal("/dev/bus/usb/003/004", d.DevicePath);
        Assert.False(d.Writable);
        Assert.Contains("ATTR{idVendor}==\"046d\", ATTR{idProduct}==\"c52b\", TAG+=\"uaccess\"", UsbHost.PermissionFix(d), StringComparison.Ordinal);
    }

    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_device_added_live_is_seen_and_a_refused_handover_says_why()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("usblive");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "usb");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "usb", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            await using var watcher = await MachineWatcher.AttachAsync(running, ct);
            await watcher.ExecuteAsync("device_add", new JsonObject { ["driver"] = "usb-kbd", ["id"] = "usb-test-keyboard" }, ct);
            Assert.Contains("usb-test-keyboard", await UsbHost.AttachedAsync(watcher, ct));
            // A host device that does not exist (bus 999): QEMU's reason comes back as an error, never silence.
            var missing = new HostUsbDevice("ffff", "ffff", "nothing", "999", "999", "x", 12, true, false);
            var ex = await Assert.ThrowsAsync<QmpException>(() => UsbHost.AttachAsync(watcher, missing, ct));
            Assert.False(string.IsNullOrWhiteSpace(ex.Description));
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }
}
