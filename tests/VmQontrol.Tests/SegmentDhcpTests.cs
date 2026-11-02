using System.Buffers.Binary;
using System.Net;
using System.Text;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

public sealed class SegmentDhcpTests
{
    private static readonly byte[] ClientMac = [0x52, 0x54, 0x00, 0x12, 0x34, 0x56];

    /// <summary>A DHCP message from a client as it goes on the wire: Ethernet, IPv4, UDP 68→67, BOOTP with options.</summary>
    private static byte[] Client(byte type, byte[] mac, uint xid, IPAddress? requested = null, IPAddress? server = null, string? hostName = null, bool broadcast = true)
    {
        var bootp = new List<byte>(new byte[240]);
        bootp[0] = 1; bootp[1] = 1; bootp[2] = 6;
        var x = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(x, xid);
        for (var i = 0; i < 4; i++) bootp[4 + i] = x[i];
        if (broadcast) bootp[10] = 0x80;
        for (var i = 0; i < 6; i++) bootp[28 + i] = mac[i];
        bootp[236] = 0x63; bootp[237] = 0x82; bootp[238] = 0x53; bootp[239] = 0x63;
        bootp.AddRange([53, 1, type]);
        if (requested is not null) bootp.AddRange([50, 4, .. requested.GetAddressBytes()]);
        if (server is not null) bootp.AddRange([54, 4, .. server.GetAddressBytes()]);
        if (hostName is not null) bootp.AddRange([12, (byte)hostName.Length, .. Encoding.ASCII.GetBytes(hostName)]);
        bootp.Add(255);
        var frame = new byte[14 + 20 + 8 + bootp.Count];
        for (var i = 0; i < 6; i++) frame[i] = 0xff;
        mac.CopyTo(frame, 6);
        frame[12] = 0x08;
        frame[14] = 0x45; frame[14 + 9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34), 68);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36), 67);
        bootp.ToArray().CopyTo(frame, 42);
        return frame;
    }

    private static (byte Type, IPAddress YourAddress, Dictionary<byte, byte[]> Options, byte[] DestinationMac) Parse(byte[] frame)
    {
        Assert.Equal(0x0800, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12)));
        var ip = frame.AsSpan(14);
        // The IPv4 header checksum must hold, or guests drop the answer.
        uint sum = 0;
        for (var i = 0; i < 20; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(ip[i..]);
        while (sum > 0xffff) sum = (sum & 0xffff) + (sum >> 16);
        Assert.Equal(0xffffu, sum);
        Assert.Equal(67, BinaryPrimitives.ReadUInt16BigEndian(ip[20..]));
        Assert.Equal(68, BinaryPrimitives.ReadUInt16BigEndian(ip[22..]));
        var bootp = ip[28..];
        Assert.Equal(2, bootp[0]);
        var options = new Dictionary<byte, byte[]>();
        for (var i = 240; i < bootp.Length && bootp[i] != 255;)
        {
            var code = bootp[i];
            var len = bootp[i + 1];
            options[code] = bootp.Slice(i + 2, len).ToArray();
            i += 2 + len;
        }

        return (options[53][0], new IPAddress(bootp.Slice(16, 4)), options, frame[..6]);
    }

    [Fact]
    public void A_machine_is_offered_an_address_acknowledged_and_keeps_it()
    {
        using var profile = new TestProfile("dhcp");
        var subnet = SegmentSubnet.Parse("10.77.3.0/24")!;
        var leases = Path.Combine(profile.Root, "leases.json");
        var server = new SegmentDhcp(subnet, leases);
        var now = DateTimeOffset.Now;

        var offer = Parse(server.Handle(Client(1, ClientMac, 0xabcd), now)!);
        Assert.Equal(2, offer.Type);
        Assert.True(subnet.Contains(offer.YourAddress));
        Assert.True(SegmentSubnet.ToUInt(offer.YourAddress) - SegmentSubnet.ToUInt(subnet.Network) >= 10, "machines start at .10");
        Assert.Equal(subnet.Mask.GetAddressBytes(), offer.Options[1]);
        Assert.Equal(subnet.Server.GetAddressBytes(), offer.Options[54]);
        Assert.False(offer.Options.ContainsKey(3), "no router: the segment stays private");
        Assert.False(offer.Options.ContainsKey(6), "no name server");
        Assert.Equal([0xff, 0xff, 0xff, 0xff, 0xff, 0xff], offer.DestinationMac); // the client asked for broadcast

        var ack = Parse(server.Handle(Client(3, ClientMac, 0xabcd, offer.YourAddress, subnet.Server, "web1"), now)!);
        Assert.Equal(5, ack.Type);
        Assert.Equal(offer.YourAddress, ack.YourAddress);
        Assert.Equal("web1", Assert.Single(server.Leases).HostName);

        // Another machine gets another address; a request for someone else's is refused.
        byte[] other = [0x52, 0x54, 0x00, 0x99, 0x88, 0x77];
        var second = Parse(server.Handle(Client(1, other, 2), now)!).YourAddress;
        Assert.NotEqual(offer.YourAddress, second);
        Assert.Equal(6, Parse(server.Handle(Client(3, other, 2, offer.YourAddress, subnet.Server), now)!).Type);
        // A request meant for another server is not answered at all.
        Assert.Null(server.Handle(Client(3, other, 2, second, IPAddress.Parse("10.77.3.254")), now));

        // After a restart of the server (the lease file read back), the first machine still gets its address.
        var again = new SegmentDhcp(subnet, leases);
        Assert.Equal(offer.YourAddress, Parse(again.Handle(Client(1, ClientMac, 9), now)!).YourAddress);
    }

    [Fact]
    public void The_server_answers_ARP_for_its_own_address_only()
    {
        var subnet = SegmentSubnet.Parse("10.77.3.0/24")!;
        var server = new SegmentDhcp(subnet, null);
        byte[] Ask(IPAddress target)
        {
            var f = new byte[42];
            for (var i = 0; i < 6; i++) f[i] = 0xff;
            ClientMac.CopyTo(f, 6);
            f[12] = 0x08; f[13] = 0x06;
            byte[] head = [0, 1, 8, 0, 6, 4, 0, 1];
            head.CopyTo(f, 14);
            ClientMac.CopyTo(f, 22);
            IPAddress.Parse("10.77.3.50").GetAddressBytes().CopyTo(f, 28);
            target.GetAddressBytes().CopyTo(f, 38);
            return f;
        }

        var reply = server.Handle(Ask(subnet.Server), DateTimeOffset.Now)!;
        Assert.Equal(ClientMac, reply[..6]);
        Assert.Equal(2, reply[21]); // an ARP reply
        Assert.Equal(subnet.Server.GetAddressBytes(), reply[28..32]);
        Assert.Equal(IPAddress.Parse("10.77.3.50").GetAddressBytes(), reply[38..42]);
        Assert.Null(server.Handle(Ask(IPAddress.Parse("10.77.3.9")), DateTimeOffset.Now));
    }

    [Theory]
    [InlineData("10.77.3.0/24", "10.77.3.0/24")]
    [InlineData("192.168.50.7/24", "192.168.50.0/24")]
    [InlineData("172.20.0.0/16", "172.20.0.0/16")]
    [InlineData("8.8.8.0/24", null)]      // not a private range
    [InlineData("10.0.0.0/8", null)]      // too big to be one segment
    [InlineData("10.0.0.0/30", null)]     // no room for machines
    [InlineData("10.0.0.0", null)]
    public void Subnets_are_private_ranges_with_room(string text, string? expected) =>
        Assert.Equal(expected, SegmentSubnet.Parse(text)?.ToString());

    [Fact]
    public void A_segment_without_a_chosen_subnet_gets_the_same_one_everywhere()
    {
        Assert.Equal(SegmentSubnet.ForName("lab").ToString(), SegmentSubnet.ForName("lab").ToString());
        Assert.NotEqual(SegmentSubnet.ForName("lab").ToString(), SegmentSubnet.ForName("office").ToString());
        Assert.NotNull(SegmentSubnet.Parse(SegmentSubnet.ForName("lab").ToString()));
    }

    /// <summary>
    /// Two machines on one segment with nothing set up in them: each asks for an address (the guest's own DHCP client)
    /// and gets one from VM Qontrol's server, and the second reaches the first at the address it was given.
    /// </summary>
    [Trait("Tier", "Guest")]
    [Fact]
    public async Task Machines_on_a_segment_get_addresses_and_reach_each_other()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("segdhcp");
        var segment = "dhcp-" + Guid.NewGuid().ToString("N")[..6];
        var shared = Path.Combine(profile.Root, "results");
        Directory.CreateDirectory(shared);
        var host = HostProbe.Probe();

        async Task<(MachineConfig, string)> MakeAsync(string name, string script, bool share)
        {
            var dir = Path.Combine(profile.Paths.DefaultLibrary, name);
            Directory.CreateDirectory(dir);
            await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
            IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
            {
                ["meta-data"] = Encoding.UTF8.GetBytes($"instance-id: vmq-{name}\nlocal-hostname: {name}\n"),
                ["user-data"] = Encoding.UTF8.GetBytes("#cloud-config\nruncmd:\n" + script),
                ["network-config"] = "version: 2\nethernets:\n  eth0:\n    dhcp4: true\n"u8.ToArray(),
            });
            var m = new MachineConfig
            {
                Name = name, MemoryMiB = 512, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
                Disks = [new DiskConfig { File = "disk0.qcow2" }],
                Media = [new MediaConfig { Role = MediaRole.Drivers, File = "seed.iso" }],
                Nics = [new NicConfig { Mode = NetMode.Segment, Segment = segment }],
                Shares = share ? [new ShareConfig { Path = shared, Tag = "results" }] : [],
            };
            return (m, dir);
        }

        var (a, aDir) = await MakeAsync("dhcp-a", "  - sleep 150\n  - poweroff\n", false);
        var ra = await MachineRunner.StartAsync(a, aDir, profile.Paths, host, new StartOptions { Headless = true }, ct);
        MachineConfig? b = null;
        try
        {
            // The first machine's lease, from the server's own record.
            var leaseFile = SegmentServer.LeaseFile(profile.Paths, segment);
            var macA = QemuCommandBuilder.MacFor(a.Id);
            SegmentLease? leaseA = null;
            for (var i = 0; i < 180 && leaseA is null; i++)
            {
                leaseA = SegmentDhcp.Load(leaseFile).FirstOrDefault(l => l.Mac == macA);
                if (leaseA is null) await Task.Delay(500, ct);
            }

            Assert.NotNull(leaseA);
            Assert.True(SegmentSubnet.ForName(segment).Contains(IPAddress.Parse(leaseA.Address)));

            var (bc, bDir) = await MakeAsync("dhcp-b", $"  - mkdir -p /mnt/r\n  - mount -t virtiofs results /mnt/r\n  - ip -4 addr show eth0 > /mnt/r/addr.txt\n  - ping -c 3 -w 90 {leaseA.Address} > /mnt/r/ping.txt 2>&1; echo \"exit $?\" >> /mnt/r/ping.txt\n  - sync\n  - poweroff\n", true);
            b = bc;
            var rb = await MachineRunner.StartAsync(b, bDir, profile.Paths, host, new StartOptions { Headless = true }, ct);
            Assert.True(await MachineRunner.WaitForExitAsync(rb, TimeSpan.FromMinutes(4), ct), "the second machine did not finish");
            var leaseB = Assert.Single(SegmentDhcp.Load(leaseFile), l => l.Mac == QemuCommandBuilder.MacFor(b.Id));
            Assert.Contains($"inet {leaseB.Address}/24", File.ReadAllText(Path.Combine(shared, "addr.txt")), StringComparison.Ordinal);
            var ping = File.ReadAllText(Path.Combine(shared, "ping.txt"));
            Assert.Contains("exit 0", ping, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var m in new[] { a, b })
                if (m is not null && MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }
    }
}

/// <summary>
/// The summary page's SSH command, run as it is shown: through the machine's forwarded port, into the guest's own SSH
/// server, with a key cloud-init put there. The guest answers with its host name.
/// </summary>
public sealed class SshForwardTests
{
    [Trait("Tier", "Guest")]
    [Fact]
    public async Task The_SSH_command_on_the_summary_signs_in_to_the_machine()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        Assert.SkipWhen(!File.Exists("/usr/bin/ssh") || !File.Exists("/usr/bin/ssh-keygen"), "OpenSSH's client is not installed.");
        using var profile = new TestProfile("sshfwd");
        var key = Path.Combine(profile.Root, "id_ed25519");
        await RunAsync("/usr/bin/ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", key], ct);
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
        Directory.CreateDirectory(dir);
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
        {
            ["meta-data"] = "instance-id: vmq-ssh-test\nlocal-hostname: vmq-ssh-guest\n"u8.ToArray(),
            ["user-data"] = Encoding.UTF8.GetBytes($"#cloud-config\nssh_authorized_keys:\n  - {File.ReadAllText(key + ".pub").Trim()}\n"),
        });
        var m = new MachineConfig
        {
            Name = "alpine", MemoryMiB = 512, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Guest = new GuestConfig { Os = "linux", Account = "alpine" },
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
            Media = [new MediaConfig { Role = MediaRole.Drivers, File = "seed.iso" }],
        };
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            Assert.NotNull(running.SshPort);
            // Exactly the words the summary shows, with the test's key and no host-key question.
            var shown = m.Guest.SshCommand(running.SshPort.Value).Split(' ');
            string[] args = ["-i", key, "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", .. shown[1..], "hostname"];
            var answer = "";
            for (var i = 0; i < 60 && !answer.Contains("vmq-ssh-guest", StringComparison.Ordinal); i++)
            {
                answer = await RunAsync("/usr/bin/ssh", args, ct, check: false);
                if (!answer.Contains("vmq-ssh-guest", StringComparison.Ordinal)) await Task.Delay(2000, ct);
            }

            Assert.Contains("vmq-ssh-guest", answer, StringComparison.Ordinal);
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    private static async Task<string> RunAsync(string program, string[] args, CancellationToken ct, bool check = true)
    {
        var start = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(start)!;
        p.StandardInput.Close();
        var output = await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (check) Assert.True(p.ExitCode == 0, output);
        return output;
    }
}
