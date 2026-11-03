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
            // A device that is not plugged in (no such port): an error that says so, never a quiet wait.
            var missing = new HostUsbDevice("ffff", "ffff", "Nothing", "999", "999", "999-9", 12, true, false);
            var ex = await Assert.ThrowsAsync<IOException>(() => UsbHost.AttachAsync(watcher, missing, ct));
            Assert.Equal("Nothing is no longer plugged in.", ex.Message);
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    [Fact]
    public void A_device_says_what_kind_it_is_from_its_interfaces()
    {
        using var profile = new TestProfile("usbclass");
        var sys = Path.Combine(profile.Root, "sys");
        Device(sys, "3-2", new() { ["idVendor"] = "0781", ["idProduct"] = "5583", ["product"] = "Ultra Fit", ["busnum"] = "3", ["devnum"] = "4", ["bDeviceClass"] = "00" });
        Device(sys, "3-2:1.0", new() { ["bInterfaceClass"] = "08" });
        Device(sys, "3-3", new() { ["idVendor"] = "046d", ["idProduct"] = "0825", ["product"] = "Webcam C270", ["busnum"] = "3", ["devnum"] = "5", ["bDeviceClass"] = "ef" });
        Device(sys, "3-3:1.0", new() { ["bInterfaceClass"] = "0e" });
        Device(sys, "3-3:1.2", new() { ["bInterfaceClass"] = "01" });
        Device(sys, "3-4", new() { ["idVendor"] = "045e", ["idProduct"] = "028e", ["product"] = "Controller", ["busnum"] = "3", ["devnum"] = "6", ["bDeviceClass"] = "ff" });
        var list = UsbHost.List(sys, _ => true);
        Assert.StartsWith("A storage device", list.Single(d => d.ProductId == "5583").Advice);
        Assert.StartsWith("A webcam", list.Single(d => d.ProductId == "0825").Advice); // video outranks its microphone
        Assert.Null(list.Single(d => d.ProductId == "028e").Advice); // a vendor-specific device: nothing to say
    }

    [Fact]
    public void A_device_plugged_in_while_watching_is_news_and_ones_already_there_are_not()
    {
        using var profile = new TestProfile("usbhotplug");
        var sys = Path.Combine(profile.Root, "sys");
        Device(sys, "3-1", new() { ["idVendor"] = "046d", ["idProduct"] = "c52b", ["busnum"] = "3", ["devnum"] = "2", ["bDeviceClass"] = "00" });
        using var hotplug = new UsbHotplug(sys, root => UsbHost.List(root, _ => true));
        hotplug.Start(TimeSpan.FromHours(1)); // checked by hand below
        Assert.Empty(hotplug.Check());

        Device(sys, "3-2", new() { ["idVendor"] = "0781", ["idProduct"] = "5583", ["product"] = "Ultra Fit", ["busnum"] = "3", ["devnum"] = "5", ["bDeviceClass"] = "00" });
        Device(sys, "3-3", new() { ["idVendor"] = "05e3", ["idProduct"] = "0610", ["busnum"] = "3", ["devnum"] = "6", ["bDeviceClass"] = "09" });
        Device(sys, "3-2:1.0", new() { ["bInterfaceClass"] = "08" });
        var plugged = Assert.Single(hotplug.Check()); // the hub and the interface are not devices to hand over
        Assert.Equal("0781:5583", plugged.Id);
        Assert.Empty(hotplug.Check());

        // Unplugged and plugged back: news again.
        Directory.Delete(Path.Combine(sys, "3-2"), true);
        Assert.Empty(hotplug.Check());
        Device(sys, "3-2", new() { ["idVendor"] = "0781", ["idProduct"] = "5583", ["busnum"] = "3", ["devnum"] = "7", ["bDeviceClass"] = "00" });
        Assert.Equal("007", Assert.Single(hotplug.Check()).Device);
    }

    /// <summary>
    /// A rule (a usb-host matched by vendor and product) is accepted by a running QEMU with no such device plugged in:
    /// it waits for one. The power-on rule carries the same id, so the panel sees both and can take them out.
    /// </summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task Rules_wait_for_their_device_and_can_be_added_and_removed_live()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("usbrule");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "rule");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig
        {
            Name = "rule", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
            Usb = new UsbConfig { Passthrough = [new UsbDevice("ffff", "fffd", "Never plugged")] },
        };
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            await using var watcher = await MachineWatcher.AttachAsync(running, ct);
            var before = await UsbHost.AttachedAsync(watcher, ct);
            Assert.Contains("usbauto-ffff-fffd", before);
            Assert.DoesNotContain("usbauto-ffff-fffe", before); // the control for the live add below
            await UsbHost.AddRuleAsync(watcher, "ffff", "fffe", ct);
            Assert.Contains("usbauto-ffff-fffe", await UsbHost.AttachedAsync(watcher, ct));
            await UsbHost.RemoveRuleAsync(watcher, "ffff", "fffe", ct);
            await UsbHost.RemoveRuleAsync(watcher, "ffff", "fffd", ct);
            // device_del finishes when the guest lets go; a USB device on an idle guest goes at once, but give it a moment.
            IReadOnlySet<string> after = before;
            for (var i = 0; i < 20 && after.Any(UsbHost.IsRuleId); i++)
            {
                await Task.Delay(100, ct);
                after = await UsbHost.AttachedAsync(watcher, ct);
            }

            Assert.DoesNotContain(after, UsbHost.IsRuleId);
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("Corsair", "CORSAIR K100 RGB", "CORSAIR K100 RGB")]
    [InlineData("Elgato", "Elgato 4K X", "Elgato 4K X")]
    [InlineData("ELAN", "ELAN:Fingerprint", "ELAN:Fingerprint")]
    [InlineData("Logitech", "USB Receiver", "Logitech USB Receiver")]
    [InlineData("", "Ultra Fit", "Ultra Fit")]
    public void A_name_says_its_maker_once(string manufacturer, string product, string expected) =>
        Assert.Equal(expected, UsbHost.DeviceName(manufacturer, product));

    /// <summary>
    /// A REAL device, only when VMQONTROL_REAL_USB=vendor:product names one that may be borrowed (never a keyboard in use):
    /// Connect opens it to this account through spice-gtk's helper with no password, QEMU takes it, and the host's own
    /// drivers let go of every interface (sysfs: no driver link); Give back returns it to them. The proof is the host's
    /// record, not ours.
    /// </summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_real_device_leaves_this_computer_and_comes_back()
    {
        var wanted = Environment.GetEnvironmentVariable("VMQONTROL_REAL_USB");
        Assert.SkipWhen(string.IsNullOrEmpty(wanted), "Set VMQONTROL_REAL_USB=vendor:product to borrow a real device.");
        var device = UsbHost.List().FirstOrDefault(d => d.Id == wanted);
        Assert.SkipWhen(device is null, $"{wanted} is not plugged in.");
        var ct = TestContext.Current.CancellationToken;
        string[] Bound() => Directory.GetDirectories("/sys/bus/usb/devices", device!.Port + ":*").Where(i => Directory.Exists(Path.Combine(i, "driver"))).ToArray();
        Assert.NotEmpty(Bound()); // the control: the host is using it now

        using var profile = new TestProfile("realusb");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "usb");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "usb", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            await using var watcher = await MachineWatcher.AttachAsync(running, ct);
            await UsbHost.AttachAsync(watcher, device!, ct);
            for (var i = 0; i < 50 && Bound().Length > 0; i++) await Task.Delay(100, ct);
            Assert.Empty(Bound());
            Assert.Contains(device!.QemuId, await UsbHost.AttachedAsync(watcher, ct));

            await UsbHost.DetachAsync(watcher, device!, ct);
            for (var i = 0; i < 50 && Bound().Length == 0; i++) await Task.Delay(100, ct);
            Assert.NotEmpty(Bound());
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }
}
