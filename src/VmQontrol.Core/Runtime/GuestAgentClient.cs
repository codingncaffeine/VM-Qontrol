using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace VmQontrol.Core.Runtime;

/// <summary>An address the guest reported for one of its network interfaces.</summary>
public sealed record GuestAddress(string Interface, string Address, bool IPv6);

/// <summary>
/// Talks to the QEMU guest agent inside a running machine (qemu-guest-agent / the virtio-win guest tools), over the
/// machine's agent socket. The agent may be absent or still starting, so every call has a timeout and a missing agent is
/// a normal answer (<c>null</c>), never an error shown to the user as a failure.
/// </summary>
public sealed class GuestAgentClient(string socketPath)
{
    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct = default) =>
        await CallAsync("guest-ping", null, timeout, ct).ConfigureAwait(false) is not null;

    public async Task<IReadOnlyList<GuestAddress>?> AddressesAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (await CallAsync("guest-network-get-interfaces", null, timeout, ct).ConfigureAwait(false) is not JsonArray interfaces) return null;
        var result = new List<GuestAddress>();
        foreach (var nic in interfaces.OfType<JsonObject>())
        {
            var name = nic["name"]?.GetValue<string>() ?? "";
            if (name == "lo" || name.StartsWith("Loopback", StringComparison.Ordinal)) continue;
            foreach (var ip in (nic["ip-addresses"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var address = ip["ip-address"]?.GetValue<string>() ?? "";
                var v6 = ip["ip-address-type"]?.GetValue<string>() == "ipv6";
                if (address.Length > 0 && !address.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)) result.Add(new GuestAddress(name, address, v6));
            }
        }

        return result;
    }

    /// <summary>The guest's total bytes received and sent on its network interfaces (loopback excluded), when the agent reports them.</summary>
    public async Task<(long In, long Out)?> AddressesAndTrafficAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (await CallAsync("guest-network-get-interfaces", null, timeout, ct).ConfigureAwait(false) is not JsonArray interfaces) return null;
        long rx = 0, tx = 0;
        var any = false;
        foreach (var nic in interfaces.OfType<JsonObject>())
        {
            var name = nic["name"]?.GetValue<string>() ?? "";
            if (name == "lo" || name.StartsWith("Loopback", StringComparison.Ordinal) || nic["statistics"] is not JsonObject stats) continue;
            rx += stats["rx-bytes"]?.GetValue<long>() ?? 0;
            tx += stats["tx-bytes"]?.GetValue<long>() ?? 0;
            any = true;
        }

        return any ? (rx, tx) : null;
    }

    /// <summary>The guest's own description of itself ("pretty-name", "kernel-release"...).</summary>
    public async Task<JsonObject?> OsInfoAsync(TimeSpan timeout, CancellationToken ct = default) =>
        await CallAsync("guest-get-osinfo", null, timeout, ct).ConfigureAwait(false) as JsonObject;

    /// <summary>Asks the guest to shut down cleanly. The agent does not answer this one; no exception means it was sent.</summary>
    public async Task<bool> ShutdownAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var socket = await OpenAsync(timeout, ct).ConfigureAwait(false);
        if (socket is null) return false;
        await SendAsync(socket, new JsonObject { ["execute"] = "guest-shutdown", ["arguments"] = new JsonObject { ["mode"] = "powerdown" } }, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<JsonNode?> CallAsync(string command, JsonObject? arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var socket = await OpenAsync(timeout, cts.Token).ConfigureAwait(false);
            if (socket is null) return null;
            // The agent may hold a half-read request from an earlier client: guest-sync-delimited resets the stream and
            // marks where the answers to this connection begin (a 0xFF byte).
            var sync = Random.Shared.NextInt64(1, int.MaxValue);
            await SendAsync(socket, new JsonObject { ["execute"] = "guest-sync-delimited", ["arguments"] = new JsonObject { ["id"] = sync } }, cts.Token).ConfigureAwait(false);
            var reader = new LineReader(socket);
            await reader.SkipPastAsync(0xFF, cts.Token).ConfigureAwait(false);
            if (JsonNode.Parse(await reader.ReadLineAsync(cts.Token).ConfigureAwait(false))?["return"]?.GetValue<long>() != sync) return null;
            var message = new JsonObject { ["execute"] = command };
            if (arguments is not null) message["arguments"] = arguments;
            await SendAsync(socket, message, cts.Token).ConfigureAwait(false);
            var answer = JsonNode.Parse(await reader.ReadLineAsync(cts.Token).ConfigureAwait(false));
            return answer?["return"];
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<Socket?> OpenAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!File.Exists(socketPath)) return null;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token).ConfigureAwait(false);
            return socket;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            socket.Dispose();
            return null;
        }
    }

    private static async Task SendAsync(Socket socket, JsonObject message, CancellationToken ct) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"), SocketFlags.None, ct).ConfigureAwait(false);

    /// <summary>Reads bytes from a socket a line at a time, without a StreamReader's read-ahead hiding the 0xFF marker.</summary>
    private sealed class LineReader(Socket socket)
    {
        private readonly byte[] _one = new byte[1];

        public async Task SkipPastAsync(byte marker, CancellationToken ct)
        {
            while (await NextAsync(ct).ConfigureAwait(false) != marker)
            {
            }
        }

        public async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var bytes = new List<byte>();
            while (true)
            {
                var b = await NextAsync(ct).ConfigureAwait(false);
                if (b == '\n') return Encoding.UTF8.GetString(bytes.ToArray());
                if (b != 0xFF) bytes.Add(b);
            }
        }

        private async Task<byte> NextAsync(CancellationToken ct)
        {
            if (await socket.ReceiveAsync(_one, SocketFlags.None, ct).ConfigureAwait(false) == 0) throw new IOException("The guest agent closed the connection.");
            return _one[0];
        }
    }
}
