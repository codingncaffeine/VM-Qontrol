using System.Globalization;

namespace VmQontrol.Core.Hosts;

/// <summary>One second of this computer: how busy its processors were, and its memory (all of it, and the machines' share).</summary>
public sealed record HostLoadSample(double CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, long MachinesBytes);

/// <summary>
/// This computer's load, as virt-manager's host graphs show it: processor time from /proc/stat between two readings,
/// memory in use = MemTotal − MemAvailable (what <c>free</c> calls used), and the resident memory of running machines.
/// </summary>
public sealed class HostLoad(string root = "/proc")
{
    private (long Busy, long Total)? _last;

    /// <summary>The "cpu" line's busy and total jiffies (busy = all but idle and iowait).</summary>
    public static (long Busy, long Total)? ParseCpu(string stat)
    {
        var line = stat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null) return null;
        var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(x => long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        if (v.Length < 5) return null;
        var total = v.Sum();
        return (total - v[3] - v[4], total);
    }

    /// <summary>The share of all processors busy between the last reading and this one; <c>null</c> on the first reading.</summary>
    public HostLoadSample? Sample(IEnumerable<int> machinePids)
    {
        string stat, meminfo;
        try
        {
            stat = File.ReadAllText(Path.Combine(root, "stat"));
            meminfo = File.ReadAllText(Path.Combine(root, "meminfo"));
        }
        catch (IOException)
        {
            return null;
        }

        var now = ParseCpu(stat);
        var last = _last;
        _last = now;
        if (now is not { } n || last is not { } l || n.Total <= l.Total) return null;
        var cpu = 100.0 * (n.Busy - l.Busy) / (n.Total - l.Total);
        long Field(string name)
        {
            var line = meminfo.Split('\n').FirstOrDefault(x => x.StartsWith(name + ":", StringComparison.Ordinal));
            var parts = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts is { Length: >= 2 } && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : 0;
        }

        var total = Field("MemTotal");
        var machines = machinePids.Sum(pid => (MemoryHeadroom.ResidentMiB(pid) ?? 0) * 1024L * 1024);
        return new HostLoadSample(Math.Clamp(cpu, 0, 100), total - Field("MemAvailable"), total, machines);
    }
}
