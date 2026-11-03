using System.Diagnostics;
using VmQontrol.Core.Hosts;

namespace VmQontrol.Tests;

public sealed class VfioTests
{
    private static PciDevice Dev(string address, string? driver, string? group = "27") =>
        new() { Address = address, Name = $"Device {address}", Class = "0x0c0330", Driver = driver, Group = group };

    [Fact]
    public void What_a_passthrough_machine_still_needs_is_named()
    {
        using var profile = new TestProfile("vfio-check");
        var dev = Path.Combine(profile.Root, "dev");
        Directory.CreateDirectory(Path.Combine(dev, "vfio"));
        var devices = new[] { Dev("0000:7c:00.0", "xhci_hcd") };
        var missing = Vfio.Missing(["0000:7c:00.0"], 8192, devices, dev, memlockBytes: 8UL << 20);
        Assert.Contains(missing, m => m.Contains("still with this computer (xhci_hcd)", StringComparison.Ordinal));
        Assert.Contains(missing, m => m.StartsWith("The memory-lock limit is 8 MB", StringComparison.Ordinal));

        // Bound, its group file open to this account, an unlimited lock: nothing left.
        devices = [Dev("0000:7c:00.0", "vfio-pci")];
        Assert.Contains(Vfio.Missing(["0000:7c:00.0"], 8192, devices, dev, ulong.MaxValue), m => m.Contains("/dev/vfio/27", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(dev, "vfio", "27"), "");
        Assert.Empty(Vfio.Missing(["0000:7c:00.0"], 8192, devices, dev, ulong.MaxValue));

        Assert.Equal(["The PCI device 0000:99:00.0 is not in this computer."], Vfio.Blocking(["0000:99:00.0"], devices));
        Assert.Contains("no IOMMU group", Assert.Single(Vfio.Blocking(["0000:7c:00.0"], [Dev("0000:7c:00.0", "xhci_hcd", group: null)])), StringComparison.Ordinal);
    }

    /// <summary>
    /// The script pkexec runs, run here on a fake sysfs: it hands the device to vfio-pci, gives the account the group
    /// file, and once "QEMU" (a short sleep) has ended, gives the device back to its own driver.
    /// </summary>
    [Fact]
    public async Task The_root_script_hands_a_device_over_and_back()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("vfio-script");
        var sys = Path.Combine(profile.Root, "sys");
        var dev = Path.Combine(profile.Root, "dev");
        var device = Path.Combine(sys, "bus", "pci", "devices", "0000:7c:00.0");
        var driver = Path.Combine(sys, "bus", "pci", "drivers", "xhci_hcd");
        var group = Path.Combine(sys, "kernel", "iommu_groups", "32");
        foreach (var d in new[] { device, driver, group, Path.Combine(dev, "vfio") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(device, "driver_override"), "");
        File.WriteAllText(Path.Combine(driver, "unbind"), "");
        File.WriteAllText(Path.Combine(sys, "bus", "pci", "drivers_probe"), "");
        File.CreateSymbolicLink(Path.Combine(device, "driver"), driver);
        File.CreateSymbolicLink(Path.Combine(device, "iommu_group"), group);
        File.WriteAllText(Path.Combine(dev, "vfio", "32"), "");
        using var qemu = Process.Start("sleep", "1")!;
        var pidfile = Path.Combine(profile.Root, "qemu.pid");
        File.WriteAllText(pidfile, qemu.Id + "\n");

        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in (string[])["-c", Vfio.Script, "sh", Environment.UserName, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), pidfile, "0000:7c:00.0"])
            start.ArgumentList.Add(a);
        start.Environment["VMQ_SYS"] = sys;
        start.Environment["VMQ_DEV"] = dev;
        start.Environment["VMQ_PRLIMIT"] = "true";
        using (var p = Process.Start(start)!)
        {
            var error = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            Assert.True(p.ExitCode == 0, error);
        }

        Assert.Equal("vfio-pci", File.ReadAllText(Path.Combine(device, "driver_override")).Trim());
        Assert.Equal("0000:7c:00.0", File.ReadAllText(Path.Combine(driver, "unbind")).Trim());
        Assert.Equal("0000:7c:00.0", File.ReadAllText(Path.Combine(sys, "bus", "pci", "drivers_probe")).Trim());

        // After "QEMU" ends, the background half gives the device back: no override, and a new probe.
        File.WriteAllText(Path.Combine(sys, "bus", "pci", "drivers_probe"), "");
        await qemu.WaitForExitAsync(ct);
        for (var i = 0; i < 50 && File.ReadAllText(Path.Combine(sys, "bus", "pci", "drivers_probe")).Trim().Length == 0; i++) await Task.Delay(100, ct);
        Assert.Equal("", File.ReadAllText(Path.Combine(device, "driver_override")).Trim());
        Assert.Equal("0000:7c:00.0", File.ReadAllText(Path.Combine(sys, "bus", "pci", "drivers_probe")).Trim());
    }

    [Fact]
    public void The_memory_lock_limit_is_read_for_this_process() => Assert.True(Vfio.MemlockLimit() > 0);
}
