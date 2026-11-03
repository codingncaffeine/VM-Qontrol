using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// Pins a running machine's processors to this computer's (vCPU thread i → PinnedCpus[i], from QEMU's own thread list)
/// and its other threads to the emulator processors, with sched_setaffinity, as libvirt's vcpupin and emulatorpin do.
/// Threads QEMU starts later inherit the emulator set from the thread that starts them.
/// </summary>
public static partial class CpuPinning
{
    /// <summary>Applies the pinning; returns what could not be done (plain sentences), empty when all of it was.</summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(QmpClient qmp, int pid, IReadOnlyList<int> pinned, IReadOnlyList<int> emulator, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var vcpuThreads = new HashSet<int>();
        if (pinned.Count > 0 && await qmp.ExecuteAsync("query-cpus-fast", ct: ct).ConfigureAwait(false) is JsonArray vcpus)
        {
            foreach (var v in vcpus.OfType<JsonObject>())
            {
                var index = v["cpu-index"]?.GetValue<int>() ?? 0;
                var tid = v["thread-id"]?.GetValue<int>() ?? 0;
                vcpuThreads.Add(tid);
                var host = pinned[index % pinned.Count];
                if (!Set(tid, [host])) problems.Add($"Processor {index} could not be pinned to {host} ({Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}).");
            }
        }

        if (emulator.Count > 0)
        {
            foreach (var task in Directory.EnumerateDirectories($"/proc/{pid}/task"))
            {
                if (!int.TryParse(Path.GetFileName(task), CultureInfo.InvariantCulture, out var tid) || vcpuThreads.Contains(tid)) continue;
                if (!Set(tid, emulator) && Directory.Exists(task)) problems.Add($"A QEMU thread could not be pinned ({Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}).");
            }
        }

        return problems.Distinct().ToList();
    }

    /// <summary>The processors a thread may run on now, from /proc (what the kernel says, not what was asked).</summary>
    public static IReadOnlyList<int> Allowed(int pid, int tid)
    {
        foreach (var line in File.ReadLines($"/proc/{pid}/task/{tid}/status"))
            if (line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal)) return Hosts.CpuTopology.ParseList(line["Cpus_allowed_list:".Length..]);
        return [];
    }

    private static unsafe bool Set(int tid, IReadOnlyList<int> cpus)
    {
        var mask = stackalloc byte[128]; // cpu_set_t: 1024 processors
        new Span<byte>(mask, 128).Clear();
        foreach (var c in cpus.Where(c => c is >= 0 and < 1024)) mask[c / 8] |= (byte)(1 << (c % 8));
        return SchedSetAffinity(tid, 128, (nint)mask) == 0;
    }

    [LibraryImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    private static partial int SchedSetAffinity(int pid, nuint size, nint mask);
}
