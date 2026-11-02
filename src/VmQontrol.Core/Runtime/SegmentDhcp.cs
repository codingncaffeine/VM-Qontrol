using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmQontrol.Core.Runtime;

/// <summary>An address handed to a machine on a segment: its MAC, the address, and the name the guest gave.</summary>
public sealed record SegmentLease(string Mac, string Address, string? HostName, DateTimeOffset Seen);

/// <summary>A segment's network: its /24 (or other) subnet, and the server's own address in it (the first host).</summary>
public sealed record SegmentSubnet(IPAddress Network, int Prefix)
{
    public IPAddress Mask => new(BinaryPrimitives.ReverseEndianness(Prefix == 0 ? 0u : uint.MaxValue << (32 - Prefix)));

    public IPAddress Server => Host(1);

    public IPAddress Broadcast => new(BinaryPrimitives.ReverseEndianness(ToUInt(Network) | ~(Prefix == 0 ? 0u : uint.MaxValue << (32 - Prefix))));

    /// <summary>Usable host numbers: 1 is the server, the rest go to machines.</summary>
    public int Hosts => (1 << (32 - Prefix)) - 2;

    public IPAddress Host(int n) => new(BinaryPrimitives.ReverseEndianness(ToUInt(Network) + (uint)n));

    public bool Contains(IPAddress a) => (ToUInt(a) & ToUInt(Mask)) == ToUInt(Network);

    public override string ToString() => $"{Network}/{Prefix}";

    public static uint ToUInt(IPAddress a) => BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes());

    /// <summary>Parses "10.77.3.0/24"; only private IPv4 ranges with room for a server and machines (/16 to /29).</summary>
    public static SegmentSubnet? Parse(string text)
    {
        var parts = text.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var prefix) || prefix is < 16 or > 29) return null;
        var mask = uint.MaxValue << (32 - prefix);
        var network = new IPAddress(BinaryPrimitives.ReverseEndianness(ToUInt(ip) & mask));
        var b = network.GetAddressBytes();
        var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and < 32) || (b[0] == 192 && b[1] == 168);
        return isPrivate ? new SegmentSubnet(network, prefix) : null;
    }

    /// <summary>The subnet a segment gets when none was chosen: 10.X.Y.0/24 from its name, the same on every computer.</summary>
    public static SegmentSubnet ForName(string segment)
    {
        var h = 2166136261u;
        foreach (var c in Encoding.UTF8.GetBytes(segment)) h = (h ^ c) * 16777619u;
        return new SegmentSubnet(new IPAddress([10, (byte)(100 + h % 100), (byte)(h / 100 % 256), 0]), 24);
    }
}

/// <summary>
/// A DHCP server (RFC 2131) and ARP responder for one segment, working on raw Ethernet frames: QEMU's multicast socket
/// network carries each frame as one UDP datagram, so VM Qontrol joins the group and sees the segment as a switch port
/// would. Each MAC keeps its address across boots (the leases are saved); no router or name server is offered, so the
/// segment stays private.
/// </summary>
public sealed class SegmentDhcp
{
    private static readonly byte[] ServerMac = [0x52, 0x54, 0x00, 0x56, 0x4d, 0x51]; // locally administered, "VMQ"
    private const uint LeaseSeconds = 86400;
    private readonly Dictionary<string, SegmentLease> _leases = new(StringComparer.Ordinal);
    private readonly string? _leaseFile;

    public SegmentDhcp(SegmentSubnet subnet, string? leaseFile)
    {
        Subnet = subnet;
        _leaseFile = leaseFile;
        foreach (var l in Load(leaseFile)) _leases[l.Mac] = l;
    }

    public SegmentSubnet Subnet { get; }

    public IReadOnlyCollection<SegmentLease> Leases => _leases.Values;

    /// <summary>The answer to one frame from the segment, or <c>null</c> when it is not for this server.</summary>
    public byte[]? Handle(ReadOnlySpan<byte> frame, DateTimeOffset now)
    {
        if (frame.Length < 14 || frame.Slice(6, 6).SequenceEqual(ServerMac)) return null; // our own frame, looped back
        var type = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
        if (type == 0x0806) return Arp(frame);
        if (type != 0x0800 || frame.Length < 14 + 20 + 8 + 240) return null;
        var ip = frame[14..];
        var ihl = (ip[0] & 0xf) * 4;
        if (ip[9] != 17 || ip.Length < ihl + 8) return null;
        var udp = ip[ihl..];
        if (BinaryPrimitives.ReadUInt16BigEndian(udp[2..]) != 67) return null;
        var bootp = udp[8..];
        if (bootp.Length < 240 || bootp[0] != 1 || BinaryPrimitives.ReadUInt32BigEndian(bootp[236..]) != 0x63825363) return null;
        var options = Options(bootp[240..]);
        if (!options.TryGetValue(53, out var kind) || kind.Length != 1) return null;
        var mac = Convert.ToHexStringLower(bootp.Slice(28, 6));
        var macText = string.Join(':', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
        options.TryGetValue(12, out var hostName);
        var name = hostName is { Length: > 0 } ? Encoding.ASCII.GetString(hostName) : null;
        var ciaddr = new IPAddress(bootp.Slice(12, 4));

        switch (kind[0])
        {
            case 1: // DISCOVER → OFFER
                return Reply(bootp, 2, Assign(macText, name, now));
            case 3: // REQUEST → ACK, or NAK for an address that is not this machine's
            {
                if (options.TryGetValue(54, out var server) && !server.AsSpan().SequenceEqual(Subnet.Server.GetAddressBytes())) return null; // chose another server
                var lease = Assign(macText, name, now);
                var asked = options.TryGetValue(50, out var req) && req.Length == 4 ? new IPAddress(req) : ciaddr;
                return asked.Equals(lease) || asked.Equals(IPAddress.Any) ? Reply(bootp, 5, lease) : Reply(bootp, 6, null);
            }
            case 8: // INFORM: the settings, no address
                return Reply(bootp, 5, null, inform: ciaddr);
            default: // RELEASE and DECLINE: the address stays this MAC's, so a machine keeps it
                return null;
        }
    }

    private IPAddress Assign(string mac, string? name, DateTimeOffset now)
    {
        if (_leases.TryGetValue(mac, out var have) && IPAddress.TryParse(have.Address, out var kept) && Subnet.Contains(kept))
        {
            if (name is not null && name != have.HostName || now - have.Seen > TimeSpan.FromMinutes(10))
            {
                _leases[mac] = have with { HostName = name ?? have.HostName, Seen = now };
                Save();
            }

            return kept;
        }

        // Machines from .10 up (below is left for addresses set by hand), the same address for a MAC every time
        // while it is free, so a fresh lease file still gives familiar addresses.
        var taken = _leases.Values.Select(l => l.Address).ToHashSet(StringComparer.Ordinal);
        var span = Math.Max(1, Subnet.Hosts - 10);
        var start = (int)(Fnv(mac) % (uint)span);
        for (var i = 0; i < span; i++)
        {
            var candidate = Subnet.Host(10 + (start + i) % span);
            if (taken.Contains(candidate.ToString())) continue;
            _leases[mac] = new SegmentLease(mac, candidate.ToString(), name, now);
            Save();
            return candidate;
        }

        throw new InvalidOperationException($"The segment's subnet {Subnet} has no free address left.");
    }

    private byte[] Reply(ReadOnlySpan<byte> request, byte type, IPAddress? yiaddr, IPAddress? inform = null)
    {
        var bootp = new byte[240 + 64];
        bootp[0] = 2; bootp[1] = 1; bootp[2] = 6;
        request.Slice(4, 4).CopyTo(bootp.AsSpan(4)); // xid
        request.Slice(10, 2).CopyTo(bootp.AsSpan(10)); // flags
        if (inform is not null) inform.GetAddressBytes().CopyTo(bootp, 12);
        (yiaddr ?? IPAddress.Any).GetAddressBytes().CopyTo(bootp, 16);
        Subnet.Server.GetAddressBytes().CopyTo(bootp, 20); // siaddr
        request.Slice(28, 16).CopyTo(bootp.AsSpan(28)); // chaddr
        BinaryPrimitives.WriteUInt32BigEndian(bootp.AsSpan(236), 0x63825363);
        var o = 240;
        void Opt(byte code, ReadOnlySpan<byte> value)
        {
            bootp[o++] = code;
            bootp[o++] = (byte)value.Length;
            value.CopyTo(bootp.AsSpan(o));
            o += value.Length;
        }

        Span<byte> u32 = stackalloc byte[4];
        Opt(53, [type]);
        Opt(54, Subnet.Server.GetAddressBytes());
        if (type != 6)
        {
            if (yiaddr is not null)
            {
                BinaryPrimitives.WriteUInt32BigEndian(u32, LeaseSeconds); Opt(51, u32);
                BinaryPrimitives.WriteUInt32BigEndian(u32, LeaseSeconds / 2); Opt(58, u32);
                BinaryPrimitives.WriteUInt32BigEndian(u32, LeaseSeconds * 7 / 8); Opt(59, u32);
            }

            Opt(1, Subnet.Mask.GetAddressBytes());
            Opt(28, Subnet.Broadcast.GetAddressBytes());
        }

        bootp[o++] = 255;
        var broadcastFlag = (request[10] & 0x80) != 0;
        var toAddress = type == 6 || broadcastFlag || yiaddr is null ? (inform ?? IPAddress.Broadcast) : yiaddr;
        var toMac = type == 6 || broadcastFlag ? [0xff, 0xff, 0xff, 0xff, 0xff, 0xff] : request.Slice(28, 6).ToArray();
        return Udp(toMac, toAddress, bootp.AsSpan(0, Math.Max(o, 300)));
    }

    private byte[] Udp(byte[] toMac, IPAddress to, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[14 + 20 + 8 + payload.Length];
        toMac.CopyTo(frame, 0);
        ServerMac.CopyTo(frame, 6);
        frame[12] = 0x08; frame[13] = 0x00;
        var ip = frame.AsSpan(14);
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)(20 + 8 + payload.Length));
        ip[8] = 64; ip[9] = 17;
        Subnet.Server.GetAddressBytes().CopyTo(ip[12..]);
        to.GetAddressBytes().CopyTo(ip[16..]);
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], Checksum(ip[..20]));
        var udp = ip[20..];
        BinaryPrimitives.WriteUInt16BigEndian(udp, 67);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], 68);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)(8 + payload.Length));
        payload.CopyTo(udp[8..]); // UDP checksum 0: none, which IPv4 allows
        return frame;
    }

    /// <summary>Answers "who has the server's address" so guests can reach it (renewals go to it directly).</summary>
    private byte[]? Arp(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 42) return null;
        var arp = frame[14..];
        if (BinaryPrimitives.ReadUInt16BigEndian(arp[6..]) != 1 || !arp.Slice(24, 4).SequenceEqual(Subnet.Server.GetAddressBytes())) return null;
        var reply = new byte[42];
        arp.Slice(8, 6).CopyTo(reply); // to the asker
        ServerMac.CopyTo(reply, 6);
        reply[12] = 0x08; reply[13] = 0x06;
        var r = reply.AsSpan(14);
        arp[..6].CopyTo(r); // hardware and protocol types and sizes
        r[7] = 2;
        ServerMac.CopyTo(r[8..]);
        Subnet.Server.GetAddressBytes().CopyTo(r[14..]);
        arp.Slice(8, 10).CopyTo(r[18..]); // the asker's MAC and address
        return reply;
    }

    private static Dictionary<byte, byte[]> Options(ReadOnlySpan<byte> o)
    {
        var map = new Dictionary<byte, byte[]>();
        for (var i = 0; i < o.Length;)
        {
            var code = o[i++];
            if (code == 0) continue;
            if (code == 255 || i >= o.Length) break;
            var len = o[i++];
            if (i + len > o.Length) break;
            map[code] = o.Slice(i, len).ToArray();
            i += len;
        }

        return map;
    }

    private static ushort Checksum(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (var i = 0; i < header.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(header[i..]);
        while (sum > 0xffff) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    private static uint Fnv(string s)
    {
        var h = 2166136261u;
        foreach (var c in s) h = (h ^ c) * 16777619u;
        return h;
    }

    private void Save()
    {
        if (_leaseFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_leaseFile)!);
            var temp = _leaseFile + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(_leases.Values.OrderBy(l => l.Address, StringComparer.Ordinal).ToList(), SegmentJson.Default.ListSegmentLease));
            File.Move(temp, _leaseFile, overwrite: true);
        }
        catch (IOException ex)
        {
            Log.Warn($"Segment leases could not be saved ({ex.Message}).");
        }
    }

    public static IReadOnlyList<SegmentLease> Load(string? file)
    {
        try
        {
            return file is not null && File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), SegmentJson.Default.ListSegmentLease) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<SegmentLease>))]
internal sealed partial class SegmentJson : JsonSerializerContext;
