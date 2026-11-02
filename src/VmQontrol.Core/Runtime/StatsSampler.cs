using System.Globalization;
using System.Text.Json.Nodes;

namespace VmQontrol.Core.Runtime;

/// <summary>One second of a running machine: how busy its processors were, its memory, and its disk and network traffic.</summary>
public sealed record StatsSample(
    DateTimeOffset At,
    double CpuPercent,
    long MemoryBytes,
    double DiskReadPerSecond,
    double DiskWritePerSecond,
    double? NetInPerSecond,
    double? NetOutPerSecond);

/// <summary>
/// Measures a running machine from the host: processor time from /proc (as a share of the processors the guest was
/// given), resident memory, disk traffic from QEMU's block statistics, and network traffic from the guest agent when it
/// answers. Call <see cref="SampleAsync"/> about once a second from a worker; the first call only primes the counters.
/// </summary>
public sealed class StatsSampler(MachineWatcher watcher, int guestCores)
{
    /// <summary>Linux reports process times in USER_HZ ticks, fixed at 100 by the kernel's user-space ABI.</summary>
    private const double TicksPerSecond = 100;

    private readonly GuestAgentClient _agent = new(watcher.Machine.Paths.Agent);
    private (DateTime At, long Ticks, long Read, long Write, long? NetIn, long? NetOut)? _last;
    private bool _agentQuiet;

    public async Task<StatsSample?> SampleAsync(CancellationToken ct = default)
    {
        var pid = watcher.Machine.Pid;
        var now = DateTime.UtcNow;
        long ticks, rss;
        try
        {
            var stat = await File.ReadAllTextAsync($"/proc/{pid}/stat", ct).ConfigureAwait(false);
            // Fields after the parenthesised process name: utime and stime are fields 14 and 15.
            var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            ticks = long.Parse(rest[11], CultureInfo.InvariantCulture) + long.Parse(rest[12], CultureInfo.InvariantCulture);
            var status = await File.ReadAllLinesAsync($"/proc/{pid}/status", ct).ConfigureAwait(false);
            var rssLine = status.FirstOrDefault(l => l.StartsWith("VmRSS:", StringComparison.Ordinal)) ?? "VmRSS: 0 kB";
            rss = long.Parse(rssLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
        }
        catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }

        long read = 0, write = 0;
        try
        {
            if (await watcher.ExecuteAsync("query-blockstats", ct: ct).ConfigureAwait(false) is JsonArray devices)
            {
                foreach (var d in devices.OfType<JsonObject>())
                {
                    read += d["stats"]?["rd_bytes"]?.GetValue<long>() ?? 0;
                    write += d["stats"]?["wr_bytes"]?.GetValue<long>() ?? 0;
                }
            }
        }
        catch (Exception ex) when (ex is QmpException or IOException)
        {
        }

        long? netIn = null, netOut = null;
        if (!_agentQuiet)
        {
            var interfaces = await _agent.AddressesAndTrafficAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            if (interfaces is null) _agentQuiet = _last is not null;
            else (netIn, netOut) = interfaces.Value;
        }

        var previous = _last;
        _last = (now, ticks, read, write, netIn, netOut);
        if (previous is not { } p) return null;
        var seconds = Math.Max(0.001, (now - p.At).TotalSeconds);
        var cpu = Math.Clamp((ticks - p.Ticks) / TicksPerSecond / seconds / Math.Max(1, guestCores) * 100, 0, 100);
        double Rate(long a, long b) => Math.Max(0, (a - b) / seconds);
        return new StatsSample(DateTimeOffset.Now, cpu, rss, Rate(read, p.Read), Rate(write, p.Write),
            netIn is { } ni && p.NetIn is { } pni ? Rate(ni, pni) : null,
            netOut is { } no && p.NetOut is { } pno ? Rate(no, pno) : null);
    }
}
