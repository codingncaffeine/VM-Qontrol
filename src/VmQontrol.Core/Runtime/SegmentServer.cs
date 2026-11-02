using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using VmQontrol.Core.Qemu;

namespace VmQontrol.Core.Runtime;

/// <summary>A private segment's settings, kept in the app's settings: its subnet and whether VM Qontrol hands out addresses.</summary>
public sealed record SegmentConfig
{
    public required string Name { get; set; }

    /// <summary>"10.77.3.0/24"; <c>null</c> = the one its name gives (<see cref="SegmentSubnet.ForName"/>).</summary>
    public string? Subnet { get; set; }

    /// <summary>Hand out addresses (on by default); off for a segment whose machines are set up by hand or run their own server.</summary>
    public bool Dhcp { get; set; } = true;
}

/// <summary>
/// Runs a segment's DHCP server: one per segment on this computer (a lock file says which process has it), started
/// with the first machine on the segment as a process of its own (so it outlives the app, as the machines do), and
/// ending once no QEMU on this computer is on the segment any more.
/// </summary>
public static class SegmentServer
{
    public const string Argument = "--segment-serve";
    private static readonly IPAddress Group = IPAddress.Parse("230.0.0.1");

    public static string LockFile(QontrolPaths paths, string segment) => Path.Combine(paths.Runtime, "segments", Safe(segment) + ".lock");

    public static string LeaseFile(QontrolPaths paths, string segment) => Path.Combine(paths.State, "segments", Safe(segment), "leases.json");

    public static SegmentSubnet SubnetFor(SegmentConfig? config, string segment) =>
        config?.Subnet is { } text && SegmentSubnet.Parse(text) is { } s ? s : SegmentSubnet.ForName(segment);

    /// <summary>Whether a server holds the segment now.</summary>
    public static bool IsRunning(QontrolPaths paths, string segment)
    {
        var file = LockFile(paths, segment);
        if (!File.Exists(file)) return false;
        try
        {
            using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Starts the segment's server unless one runs: the same program (vmq or vmqontrol) with <see cref="Argument"/>, in
    /// a session of its own. Where this is not one of VM Qontrol's programs (a test host), the server runs in this
    /// process for as long as it does.
    /// </summary>
    public static void EnsureRunning(QontrolPaths paths, string segment, SegmentConfig? config)
    {
        if (config is { Dhcp: false } || IsRunning(paths, segment)) return;
        var program = Environment.ProcessPath ?? "";
        var entry = Assembly.GetEntryAssembly()?.Location ?? "";
        var ours = Path.GetFileName(program) is "vmq" or "vmqontrol" || Path.GetFileNameWithoutExtension(entry) is "vmq" or "VmQontrol.App" or "vmqontrol";
        if (!ours)
        {
            _ = Task.Run(() => RunAsync(paths, segment, SubnetFor(config, segment), CancellationToken.None));
            return;
        }

        var start = new ProcessStartInfo("setsid") { UseShellExecute = false, RedirectStandardOutput = false, RedirectStandardError = false };
        start.ArgumentList.Add(program);
        if (Path.GetFileName(program) is "dotnet") start.ArgumentList.Add(entry);
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(segment);
        start.ArgumentList.Add(SubnetFor(config, segment).ToString());
        try
        {
            using var _ = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn($"The segment {segment}'s address server could not start ({ex.Message}); its machines get no address.");
        }
    }

    /// <summary>The entry point for <see cref="Argument"/>: <c>--segment-serve NAME SUBNET</c>.</summary>
    public static int Main(QontrolPaths paths, string[] args)
    {
        var i = Array.IndexOf(args, Argument);
        if (i < 0 || i + 2 >= args.Length || SegmentSubnet.Parse(args[i + 2]) is not { } subnet) return 2;
        RunAsync(paths, args[i + 1], subnet, CancellationToken.None).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>Serves the segment until no machine is on it (or <paramref name="ct"/> ends it).</summary>
    public static async Task RunAsync(QontrolPaths paths, string segment, SegmentSubnet subnet, CancellationToken ct)
    {
        var lockPath = LockFile(paths, segment);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        FileStream held;
        try
        {
            held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return; // another process serves it
        }

        await using (held)
        {
            held.SetLength(0);
            await held.WriteAsync(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
            await held.FlushAsync(ct).ConfigureAwait(false);
            var port = QemuCommandBuilder.SegmentPort(segment);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, IPAddress.Loopback));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            var server = new SegmentDhcp(subnet, LeaseFile(paths, segment));
            var to = new IPEndPoint(Group, port);
            Log.Info($"Segment {segment}: handing out addresses in {subnet} (server {subnet.Server}).");
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = WatchForMachinesAsync(port, idle);
            var buffer = new byte[65536];
            try
            {
                while (!idle.IsCancellationRequested)
                {
                    var n = await socket.ReceiveAsync(buffer, SocketFlags.None, idle.Token).ConfigureAwait(false);
                    byte[]? reply;
                    try
                    {
                        reply = server.Handle(buffer.AsSpan(0, n), DateTimeOffset.Now);
                    }
                    catch (InvalidOperationException ex)
                    {
                        Log.Warn($"Segment {segment}: {ex.Message}");
                        continue;
                    }

                    if (reply is not null) await socket.SendToAsync(reply, SocketFlags.None, to, idle.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            Log.Info($"Segment {segment}: no machine is on it any more; the address server stopped.");
        }

        try
        {
            File.Delete(lockPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Ends the server once no QEMU process on this computer has the segment's group on its command line (twice in a row).</summary>
    private static async Task WatchForMachinesAsync(int port, CancellationTokenSource stop)
    {
        var needle = $"mcast=230.0.0.1:{port},";
        var empty = 0;
        // A first look after a grace period: the machine that started the server may still be on its way up.
        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        while (!stop.IsCancellationRequested)
        {
            empty = AnyOnSegment(needle) ? 0 : empty + 1;
            if (empty >= 2)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
    }

    private static bool AnyOnSegment(string needle)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out _)) continue;
            try
            {
                if (File.ReadAllText(Path.Combine(dir, "cmdline")).Contains(needle, StringComparison.Ordinal)) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private static string Safe(string segment) => string.Concat(segment.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
