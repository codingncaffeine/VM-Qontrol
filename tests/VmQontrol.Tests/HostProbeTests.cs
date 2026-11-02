using VmQontrol.Core.Hosts;

namespace VmQontrol.Tests;

public sealed class HostProbeTests
{
    [Fact]
    public void Memory_truncates_like_free_giga() =>
        Assert.Equal(64, HostProbe.MemoryGigabytes("MemTotal:       63389236 kB\nMemFree: 1 kB\n"));

    [Fact]
    public void Device_list_includes_aliases()
    {
        var names = HostProbe.ParseDeviceNames("name \"virtio-gpu-gl-pci\", bus PCI, alias \"virtio-gpu-gl\"\nname \"qxl-vga\", bus PCI\n");
        Assert.Contains("virtio-gpu-gl", names);
        Assert.Contains("virtio-gpu-gl-pci", names);
        Assert.Contains("qxl-vga", names);
    }

    [Fact]
    public void Qemu_version_line() =>
        Assert.Equal(new Version(11, 1, 3), HostProbe.ParseQemuVersion("QEMU emulator version 11.1.3\nCopyright (c) 2003-2026"));

    [Trait("Tier", "Integration")]
    [Fact]
    public void The_real_host_probe_answers()
    {
        var host = HostProbe.Probe();
        Assert.True(host.LogicalCpus > 0);
        Assert.True(host.MemoryGB > 0);
        Assert.True(host.QemuVersion.Major > 0, "qemu-system-x86_64 --version gave no version");
        Assert.Contains("virtio-blk-pci", host.QemuDevices);
    }
}
