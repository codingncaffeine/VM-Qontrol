using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace VmQontrol.Core.Runtime;

/// <summary>An event QEMU sent (SHUTDOWN, STOP, RESUME, RESET, GUEST_PANICKED...).</summary>
public sealed record QmpEvent(string Name, JsonObject? Data, DateTimeOffset At);

/// <summary>QEMU refused a command; <see cref="Class"/> is QEMU's error class (GenericError, CommandNotFound...).</summary>
public sealed class QmpException(string command, string @class, string description)
    : Exception($"QEMU refused '{command}': {description}")
{
    public string Class { get; } = @class;

    public string Description { get; } = description;
}

/// <summary>
/// A connection to a machine's QMP socket: QEMU's JSON machine protocol, one message per line. Commands are matched to
/// their answers by id, events arrive on <see cref="Events"/>, and <see cref="Closed"/> completes when QEMU goes away.
/// Everything here is asynchronous; nothing blocks the caller's thread.
/// </summary>
public sealed class QmpClient : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly StreamReader _reader;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, (string Command, TaskCompletionSource<JsonNode?> Answer)> _pending = new();
    private readonly Channel<QmpEvent> _events = Channel.CreateUnbounded<QmpEvent>(new UnboundedChannelOptions { SingleReader = false });
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;

    private QmpClient(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _reader = new StreamReader(_stream, Encoding.UTF8);
    }

    public ChannelReader<QmpEvent> Events => _events.Reader;

    public Task Closed => _closed.Task;

    /// <summary>The version QEMU announced in its greeting.</summary>
    public string QemuVersion { get; private set; } = "";

    /// <summary>Connects, waiting up to <paramref name="timeout"/> for the socket to appear, and enters command mode.</summary>
    public static async Task<QmpClient> ConnectAsync(string socketPath, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                var client = new QmpClient(socket);
                await client.HandshakeAsync(ct).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or IOException && DateTime.UtcNow < deadline)
            {
                socket.Dispose();
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        var greeting = await _reader.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new IOException("QEMU closed the QMP socket before greeting.");
        var version = JsonNode.Parse(greeting)?["QMP"]?["version"]?["qemu"];
        QemuVersion = version is null ? "" : $"{version["major"]}.{version["minor"]}.{version["micro"]}";
        _ = Task.Run(ReadLoopAsync, CancellationToken.None);
        await ExecuteAsync("qmp_capabilities", ct: ct).ConfigureAwait(false);
    }

    /// <summary>Runs a command and returns its "return" value; throws <see cref="QmpException"/> when QEMU refuses it.</summary>
    public async Task<JsonNode?> ExecuteAsync(string command, JsonObject? arguments = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (command, answer);
        var message = new JsonObject { ["execute"] = command, ["id"] = id };
        if (arguments is not null) message["arguments"] = arguments;
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed.Task.IsCompleted) throw new IOException("The machine has stopped; its QMP socket is closed.");
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        using var _ = ct.Register(() => answer.TrySetCanceled(ct));
        return await answer.Task.ConfigureAwait(false);
    }

    /// <summary>A command whose answer QEMU may not send because it exits first (quit).</summary>
    public async Task ExecuteExpectingExitAsync(string command, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync(command, ct: ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                if (JsonNode.Parse(line) is not JsonObject message) continue;
                if (message["event"]?.GetValue<string>() is { } name)
                {
                    var ts = message["timestamp"];
                    var at = ts is null ? DateTimeOffset.Now : DateTimeOffset.FromUnixTimeMilliseconds((long)(ts["seconds"]!.GetValue<long>() * 1000 + ts["microseconds"]!.GetValue<long>() / 1000));
                    _events.Writer.TryWrite(new QmpEvent(name, message["data"] as JsonObject, at));
                    continue;
                }

                if (message["id"]?.GetValue<long>() is not { } id || !_pending.TryRemove(id, out var pending)) continue;
                if (message["error"] is JsonObject error)
                    pending.Answer.TrySetException(new QmpException(pending.Command, error["class"]?.GetValue<string>() ?? "", error["desc"]?.GetValue<string>() ?? ""));
                else
                    pending.Answer.TrySetResult(message["return"]);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Text.Json.JsonException or SocketException)
        {
        }
        finally
        {
            foreach (var (_, pending) in _pending) pending.Answer.TrySetException(new IOException("The machine has stopped; its QMP socket closed."));
            _pending.Clear();
            _events.Writer.TryComplete();
            _closed.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _reader.Dispose();
        _stream.Dispose();
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
