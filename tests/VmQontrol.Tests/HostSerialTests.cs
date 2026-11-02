using System.Diagnostics;
using System.Text;
using VmQontrol.Core.Devices;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

public sealed class HostSerialTests
{
    private static void Tty(string sys, string name, Dictionary<string, string> files)
    {
        var dir = Path.Combine(sys, "class", "tty", name);
        Directory.CreateDirectory(dir);
        foreach (var (f, v) in files) File.WriteAllText(Path.Combine(dir, f), v + "\n");
    }

    [Fact]
    public void Ports_with_a_UART_behind_them_and_USB_adapters_are_listed()
    {
        using var profile = new TestProfile("serial");
        var sys = Path.Combine(profile.Root, "sys");
        var dev = Path.Combine(profile.Root, "dev");
        Tty(sys, "ttyS0", new() { ["type"] = "4", ["dev"] = "4:64" });
        Tty(sys, "ttyS1", new() { ["type"] = "0", ["dev"] = "4:65" }); // the kernel's placeholder: no UART answered
        Tty(sys, "ttyUSB0", new() { ["dev"] = "188:0" });
        Tty(sys, "tty1", new() { ["dev"] = "4:1" });
        Directory.CreateDirectory(Path.Combine(dev, "serial", "by-id"));
        File.WriteAllText(Path.Combine(dev, "ttyUSB0"), "");
        File.CreateSymbolicLink(Path.Combine(dev, "serial", "by-id", "usb-FTDI_FT232R_A1B2-if00-port0"), "../../ttyUSB0");
        var ports = HostSerial.List(sys, dev);
        Assert.Equal(["ttyS0", "ttyUSB0"], ports.Select(p => p.Name));
        Assert.Equal("COM1 (16550A)", ports[0].Description);
        Assert.False(ports[0].IsUsb);
        Assert.True(ports[1].IsUsb);
        Assert.Equal(Path.Combine(dev, "serial", "by-id", "usb-FTDI_FT232R_A1B2-if00-port0"), ports[1].StablePath);
    }

    [Fact]
    public void A_host_port_becomes_the_next_COM_port_on_the_command_line()
    {
        var m = new MachineConfig { Name = "s", Disks = [new DiskConfig { File = "d.qcow2" }], HostSerialPorts = [new HostSerialConfig { Path = "/dev/ttyS0" }] };
        var ctx = new Core.Qemu.LaunchContext { Host = RecordedHost.Load(), MachineDirectory = "/m", Runtime = new Core.Qemu.RuntimePaths("/r"), Headless = true };
        var args = Core.Qemu.QemuCommandBuilder.Build(m, ctx).Args;
        var at = args.ToList().IndexOf("serial,id=hostserial0,path=/dev/ttyS0");
        Assert.True(at > 0 && args[at - 1] == "-chardev");
        Assert.Equal("isa-serial,chardev=hostserial0,id=hostserial0-port", args[at + 2]);
        Assert.True(args.ToList().IndexOf("-serial") < at, "the console's -serial keeps COM1");
    }

    [Fact]
    public void Group_membership_is_read_from_the_group_file()
    {
        using var profile = new TestProfile("groups");
        var file = Path.Combine(profile.Root, "group");
        File.WriteAllText(file, "wheel:x:10:alice\nuucp:x:987:bob,alice\ndialout:x:20:\n");
        Assert.True(HostSerial.Listed("uucp", "alice", file));
        Assert.False(HostSerial.Listed("dialout", "alice", file));
        Assert.False(HostSerial.Listed("uucp", "carol", file));
    }

    /// <summary>
    /// The guest writes to its COM2 and the host reads it from the other end of a pseudo-terminal pair: the same path a
    /// real serial port takes (QEMU's serial character device on a tty), with no hardware needed. A port that is not
    /// there is left out with a notice, and the machine still starts.
    /// </summary>
    [Trait("Tier", "Guest")]
    [Fact]
    public async Task The_guest_writes_to_a_host_serial_port()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        Assert.SkipWhen(!File.Exists("/usr/bin/socat"), "socat makes the pseudo-terminal pair");
        using var profile = new TestProfile("serialguest");
        var machineEnd = Path.Combine(profile.Root, "machine-end");
        var hostEnd = Path.Combine(profile.Root, "host-end");
        var socat = Process.Start(new ProcessStartInfo("/usr/bin/socat") { ArgumentList = { $"pty,raw,echo=0,link={machineEnd}", $"pty,raw,echo=0,link={hostEnd}" }, RedirectStandardError = true })!;
        try
        {
            for (var i = 0; i < 50 && !(File.Exists(machineEnd) && File.Exists(hostEnd)); i++) await Task.Delay(100, ct);
            var target = File.ResolveLinkTarget(machineEnd, true)!.FullName; // the pts device itself, as a port's path is
            using var reader = new FileStream(File.ResolveLinkTarget(hostEnd, true)!.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, useAsync: false);

            var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
            Directory.CreateDirectory(dir);
            await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
            IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
            {
                ["meta-data"] = "instance-id: vmq-serial-test\n"u8.ToArray(),
                ["user-data"] = Encoding.UTF8.GetBytes("""
                    #cloud-config
                    runcmd:
                      - stty -F /dev/ttyS1 raw -echo 115200
                      - printf 'VMQ-SERIAL hello from COM2\n' > /dev/ttyS1
                      - sleep 2
                      - poweroff
                    """),
            });
            var m = new MachineConfig
            {
                Name = "alpine", MemoryMiB = 512, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
                Disks = [new DiskConfig { File = "disk0.qcow2" }],
                Media = [new MediaConfig { Role = MediaRole.Drivers, File = "seed.iso" }],
                HostSerialPorts = [new HostSerialConfig { Path = target, Name = "test port" }, new HostSerialConfig { Path = "/dev/serial/by-id/usb-Not_Here-if00-port0", Name = "An adapter that is not here" }],
            };
            var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
            Assert.Equal(["An adapter that is not here is not connected to this computer."], running.Notices);
            var read = Task.Run(() =>
            {
                var seen = new StringBuilder();
                var b = new byte[256];
                while (!seen.ToString().Contains('\n'))
                {
                    var n = reader.Read(b, 0, b.Length);
                    if (n <= 0) break;
                    seen.Append(Encoding.ASCII.GetString(b, 0, n));
                }

                return seen.ToString();
            }, ct);
            try
            {
                Assert.True(await MachineRunner.WaitForExitAsync(running, TimeSpan.FromMinutes(3), ct), "the guest did not finish and power off");
            }
            finally
            {
                if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
            }

            var line = await read.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Contains("VMQ-SERIAL hello from COM2", line);
        }
        finally
        {
            socat.Kill();
            socat.Dispose();
        }
    }
}
