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
    /// <summary>The guest's own words for why the last call was refused ("No such file or directory"); <c>null</c> after a success.</summary>
    public string? LastError { get; private set; }

    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct = default) =>
        await CallAsync("guest-ping", null, timeout, ct).ConfigureAwait(false) is not null;

    public async Task<IReadOnlyList<GuestAddress>?> AddressesAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (await CallAsync("guest-network-get-interfaces", null, timeout, ct).ConfigureAwait(false) is not JsonArray interfaces) return null;
        var result = new List<GuestAddress>();
        foreach (var nic in interfaces.OfType<JsonObject>())
        {
            var name = nic["name"].Str() ?? "";
            if (name == "lo" || name.StartsWith("Loopback", StringComparison.Ordinal)) continue;
            foreach (var ip in (nic["ip-addresses"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var address = ip["ip-address"].Str() ?? "";
                var v6 = ip["ip-address-type"].Str() == "ipv6";
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
            var name = nic["name"].Str() ?? "";
            if (name == "lo" || name.StartsWith("Loopback", StringComparison.Ordinal) || nic["statistics"] is not JsonObject stats) continue;
            rx += stats["rx-bytes"].Long() ?? 0;
            tx += stats["tx-bytes"].Long() ?? 0;
            any = true;
        }

        return any ? (rx, tx) : null;
    }

    /// <summary>The guest's own description of itself ("pretty-name", "kernel-release"...).</summary>
    public async Task<JsonObject?> OsInfoAsync(TimeSpan timeout, CancellationToken ct = default) =>
        await CallAsync("guest-get-osinfo", null, timeout, ct).ConfigureAwait(false) as JsonObject;

    /// <summary>Reads a small file in the guest through the agent (at most 64 KiB); <c>null</c> when the agent did not answer or refused.</summary>
    public async Task<byte[]?> ReadFileAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        if (await CallAsync("guest-file-open", new JsonObject { ["path"] = path, ["mode"] = "r" }, timeout, ct).ConfigureAwait(false) is not JsonValue handleValue
            || !handleValue.TryGetValue<long>(out var handle))
            return null;
        try
        {
            var read = await CallAsync("guest-file-read", new JsonObject { ["handle"] = handle, ["count"] = 65536 }, timeout, ct).ConfigureAwait(false);
            return read?["buf-b64"].Str() is { } b64 ? GuestJson.Base64(b64) : null;
        }
        finally
        {
            await CallAsync("guest-file-close", new JsonObject { ["handle"] = handle }, timeout, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>What a program run inside the guest left: its exit code (null when it was still running at the timeout) and output.</summary>
    public sealed record ExecResult(int? ExitCode, string Output, string Errors, bool TimedOut);

    /// <summary>
    /// Runs a program inside the guest and waits for it (VMware's vmrun runProgramInGuest, VirtualBox's guestcontrol run):
    /// <paramref name="path"/> is the program as the guest names it, its output is captured. <c>null</c> when the agent did
    /// not answer or refused (Windows' agent refuses unless guest-exec is allowed; Linux distributions ship it allowed).
    /// </summary>
    public async Task<ExecResult?> ExecAsync(string path, IReadOnlyList<string> arguments, byte[]? input, TimeSpan runFor, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["path"] = path,
            ["arg"] = new JsonArray([.. arguments.Select(a => (JsonNode)a)]),
            ["capture-output"] = true,
        };
        if (input is not null) request["input-data"] = Convert.ToBase64String(input);
        if (await CallAsync("guest-exec", request, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false) is not JsonObject started
            || started["pid"].Long() is not { } pid)
            return null;
        var until = DateTime.UtcNow + runFor;
        var delay = TimeSpan.FromMilliseconds(50);
        while (true)
        {
            if (await CallAsync("guest-exec-status", new JsonObject { ["pid"] = pid }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false) is not JsonObject status) return null;
            if (status["exited"].Bool() == true)
                return new ExecResult(status["exitcode"].Int() ?? (status["signal"] is not null ? 128 + status["signal"].Int() : null),
                    Decode(status["out-data"]), Decode(status["err-data"]), false);
            if (DateTime.UtcNow >= until) return new ExecResult(null, "", "", true);
            await Task.Delay(delay, ct).ConfigureAwait(false);
            if (delay < TimeSpan.FromMilliseconds(500)) delay *= 2;
        }

        static string Decode(JsonNode? b64) => b64.Str() is { } s ? Encoding.UTF8.GetString(GuestJson.Base64(s)) : "";
    }

    /// <summary>The agent moves files in pieces of this size (its own limit on one read is 48 MiB; small pieces keep each call short).</summary>
    private const int Chunk = 1 << 20;

    /// <summary>
    /// Copies <paramref name="source"/> into the guest as <paramref name="guestPath"/> (created or replaced). Returns the
    /// bytes written, or <c>null</c> when the agent did not answer or refused (no such folder, no permission).
    /// </summary>
    public async Task<long?> WriteFileAsync(string guestPath, Stream source, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (await CallAsync("guest-file-open", new JsonObject { ["path"] = guestPath, ["mode"] = "wb" }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false) is not JsonValue handleValue
            || !handleValue.TryGetValue<long>(out var handle))
            return null;
        long total = 0;
        try
        {
            var buffer = new byte[Chunk];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                var wrote = await CallAsync("guest-file-write", new JsonObject { ["handle"] = handle, ["buf-b64"] = Convert.ToBase64String(buffer, 0, read) }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                if (wrote?["count"].Long() != read) return null;
                total += read;
                progress?.Report(total);
            }

            return await CallAsync("guest-file-flush", new JsonObject { ["handle"] = handle }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false) is null ? null : total;
        }
        finally
        {
            await CallAsync("guest-file-close", new JsonObject { ["handle"] = handle }, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Copies the guest's file <paramref name="guestPath"/> into <paramref name="target"/>; the bytes read, or <c>null</c> when refused.</summary>
    public async Task<long?> ReadFileAsync(string guestPath, Stream target, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (await CallAsync("guest-file-open", new JsonObject { ["path"] = guestPath, ["mode"] = "rb" }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false) is not JsonValue handleValue
            || !handleValue.TryGetValue<long>(out var handle))
            return null;
        long total = 0;
        try
        {
            while (true)
            {
                if (await CallAsync("guest-file-read", new JsonObject { ["handle"] = handle, ["count"] = Chunk }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false) is not JsonObject read) return null;
                if (read["buf-b64"].Str() is { Length: > 0 } b64)
                {
                    var bytes = GuestJson.Base64(b64);
                    await target.WriteAsync(bytes, ct).ConfigureAwait(false);
                    total += bytes.Length;
                    progress?.Report(total);
                }

                if (read["eof"].Bool() == true) return total;
            }
        }
        finally
        {
            await CallAsync("guest-file-close", new JsonObject { ["handle"] = handle }, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Sets the guest's clock to this computer's (after a resume, when the guest's clock stood still); <c>false</c> when the agent did not answer.</summary>
    public async Task<bool> SetTimeAsync(DateTimeOffset now, TimeSpan timeout, CancellationToken ct = default) =>
        await CallAsync("guest-set-time", new JsonObject { ["time"] = (now - DateTimeOffset.UnixEpoch).Ticks * 100 }, timeout, ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// Switches on every processor the guest has but keeps offline (Linux leaves processors added while running offline
    /// unless a udev rule onlines them). Returns how many are online, or <c>null</c> when the agent did not answer.
    /// </summary>
    public async Task<int?> OnlineAllProcessorsAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (await CallAsync("guest-get-vcpus", null, timeout, ct).ConfigureAwait(false) is not JsonArray cpus) return null;
        var offline = cpus.OfType<JsonObject>().Where(c => c["online"].Bool() == false && c["can-offline"] is not null).ToList();
        if (offline.Count > 0)
        {
            var set = new JsonArray([.. offline.Select(c => (JsonNode)new JsonObject { ["logical-id"] = (c["logical-id"].Long() ?? 0), ["online"] = true })]);
            await CallAsync("guest-set-vcpus", new JsonObject { ["vcpus"] = set }, timeout, ct).ConfigureAwait(false);
            if (await CallAsync("guest-get-vcpus", null, timeout, ct).ConfigureAwait(false) is JsonArray again) cpus = again;
        }

        return cpus.OfType<JsonObject>().Count(c => c["online"].Bool() == true);
    }

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
            LastError = null;
            if (socket is null)
            {
                LastError = "The guest agent is not answering (the guest tools are not installed or not started yet).";
                return null;
            }

            // The agent may hold a half-read request from an earlier client: guest-sync-delimited resets the stream and
            // marks where the answers to this connection begin (a 0xFF byte).
            var sync = Random.Shared.NextInt64(1, int.MaxValue);
            await SendAsync(socket, new JsonObject { ["execute"] = "guest-sync-delimited", ["arguments"] = new JsonObject { ["id"] = sync } }, cts.Token).ConfigureAwait(false);
            var reader = new LineReader(socket);
            await reader.SkipPastAsync(0xFF, cts.Token).ConfigureAwait(false);
            if (JsonNode.Parse(await reader.ReadLineAsync(cts.Token).ConfigureAwait(false))?["return"].Long() != sync) return null;
            var message = new JsonObject { ["execute"] = command };
            if (arguments is not null) message["arguments"] = arguments;
            await SendAsync(socket, message, cts.Token).ConfigureAwait(false);
            var answer = JsonNode.Parse(await reader.ReadLineAsync(cts.Token).ConfigureAwait(false));
            LastError = answer?["error"]?["desc"].Str();
            return answer?["return"];
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            LastError = ex is OperationCanceledException ? "The guest agent did not answer in time." : $"The guest agent stopped answering ({ex.Message}).";
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
        private const int MaxLine = 64 << 20;

        private readonly byte[] _buffer = new byte[65536];
        private int _start, _end;

        public async Task SkipPastAsync(byte marker, CancellationToken ct)
        {
            while (await NextAsync(ct).ConfigureAwait(false) != marker)
            {
            }
        }

        public async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var bytes = new MemoryStream();
            while (true)
            {
                if (_start == _end) await FillAsync(ct).ConfigureAwait(false);
                var span = _buffer.AsSpan(_start, _end - _start);
                var newline = span.IndexOf((byte)'\n');
                var take = newline < 0 ? span : span[..newline];
                foreach (var b in take)
                    if (b != 0xFF) bytes.WriteByte(b);
                // A guest that never ends its line is not answering: past this size it is cut off, not held in memory.
                if (bytes.Length > MaxLine) throw new IOException($"The guest agent sent more than {MaxLine >> 20} MiB in one answer.");
                _start += take.Length;
                if (newline >= 0)
                {
                    _start++;
                    return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
                }
            }
        }

        private async Task<byte> NextAsync(CancellationToken ct)
        {
            if (_start == _end) await FillAsync(ct).ConfigureAwait(false);
            return _buffer[_start++];
        }

        private async Task FillAsync(CancellationToken ct)
        {
            _start = 0;
            _end = await socket.ReceiveAsync(_buffer, SocketFlags.None, ct).ConfigureAwait(false);
            if (_end == 0) throw new IOException("The guest agent closed the connection.");
        }
    }
}

/// <summary>
/// Reading what the guest sent: the guest decides what its agent answers, so a value of the wrong type (a number where a
/// name belongs) or broken base64 reads as missing instead of failing the caller.
/// </summary>
internal static class GuestJson
{
    public static string? Str(this JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? Long(this JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    public static int? Int(this JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public static bool? Bool(this JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static byte[] Base64(string text)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
