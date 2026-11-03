using System.Globalization;

namespace VmQontrol.Core.Hosts;

/// <summary>
/// Starting a machine would leave this computer with <see cref="AfterMiB"/> free, less than the <see cref="KeepFreeMiB"/>
/// the person keeps for it (Preferences → Memory).
/// </summary>
/// <param name="WantMiB">The machine's memory.</param>
/// <param name="AvailableMiB">What Linux says it can hand out now (MemAvailable).</param>
/// <param name="PromisedMiB">Memory running machines were given but have not touched yet; they take it as they run.</param>
/// <param name="KeepFreeMiB">What stays free for the computer itself.</param>
public sealed record MemoryShortfall(long WantMiB, long AvailableMiB, long PromisedMiB, long KeepFreeMiB)
{
    public long AfterMiB => AvailableMiB - PromisedMiB - WantMiB;
}

/// <summary>
/// The memory check before a machine powers on (Workstation's reserved-memory warning, made honest): QEMU takes a
/// machine's memory only as the guest touches it, so a machine that has just started still has most of its memory to
/// come. The check counts what is free now, minus what running machines can still claim, minus this machine.
/// </summary>
public static class MemoryHeadroom
{
    /// <summary>What stays free when the person has not chosen: an eighth of the computer, between 4 and 16 GB.</summary>
    public static long AutomaticKeepFreeMiB(long totalMiB) => Math.Clamp(totalMiB / 8, 4096, 16384);

    /// <summary>MemTotal and MemAvailable from <c>/proc/meminfo</c>, in MiB (0 when missing).</summary>
    public static (long TotalMiB, long AvailableMiB) Read(string meminfo) => (Field(meminfo, "MemTotal"), Field(meminfo, "MemAvailable"));

    /// <summary>This computer's memory now; (0, 0) when <c>/proc/meminfo</c> cannot be read.</summary>
    public static (long TotalMiB, long AvailableMiB) ReadHost()
    {
        try
        {
            return Read(File.ReadAllText("/proc/meminfo"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    /// <summary>The memory a process holds now (VmRSS), in MiB; <c>null</c> when it has gone.</summary>
    public static long? ResidentMiB(int pid)
    {
        try
        {
            var value = Field(File.ReadAllText($"/proc/{pid}/status"), "VmRSS");
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What a running machine can still claim: its memory less what its QEMU already holds, never below zero.</summary>
    public static long Promised(long memoryMiB, long residentMiB) => Math.Max(0, memoryMiB - residentMiB);

    /// <summary>The shortfall when starting <paramref name="wantMiB"/> leaves less than <paramref name="keepFreeMiB"/>; <c>null</c> when it fits or memory is unknown.</summary>
    public static MemoryShortfall? Check(long wantMiB, long availableMiB, long promisedMiB, long keepFreeMiB)
    {
        if (availableMiB <= 0 || wantMiB <= 0) return null;
        var shortfall = new MemoryShortfall(wantMiB, availableMiB, promisedMiB, keepFreeMiB);
        return shortfall.AfterMiB >= keepFreeMiB ? null : shortfall;
    }

    /// <summary>The warning in words, with the numbers.</summary>
    public static IReadOnlyList<string> Describe(MemoryShortfall s, string name)
    {
        var lines = new List<string>();
        var free = s.PromisedMiB > 0
            ? $"This computer has {Gb(s.AvailableMiB)} free now, and machines already running can still take {Gb(s.PromisedMiB)} of it as they work."
            : $"This computer has {Gb(s.AvailableMiB)} free now.";
        lines.Add($"{name} needs {Gb(s.WantMiB)} of memory. {free}");
        lines.Add(s.AfterMiB <= 0
            ? "There is not enough memory for it. When memory runs out, Linux stops the largest program, which may be this machine or another one, and anything not saved in it is lost."
            : $"That leaves {Gb(s.AfterMiB)}, less than the {Gb(s.KeepFreeMiB)} kept free for the computer itself (Preferences → Memory). The desktop and programs may slow down, and if memory runs out Linux stops the largest program.");
        lines.Add(s.PromisedMiB > 0
            ? "Giving this machine less memory in Settings → Memory, or shutting another machine down, avoids it."
            : "Giving this machine less memory in Settings → Memory avoids it.");
        return lines;
    }

    public static string Gb(long mib) => mib >= 1024
        ? (mib / 1024d).ToString(mib % 1024 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " GB"
        : $"{mib} MB";

    private static long Field(string text, string name)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith(name + ":", StringComparison.Ordinal)) continue;
            var parts = line[(name.Length + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)) return kb / 1024;
        }

        return 0;
    }
}
