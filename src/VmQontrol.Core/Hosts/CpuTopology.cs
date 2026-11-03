using System.Globalization;

namespace VmQontrol.Core.Hosts;

/// <summary>One logical processor of this computer: its core, the processors sharing that core, and its L3 cache group.</summary>
public sealed record HostCpu(int Id, int Core, int Die, IReadOnlyList<int> Siblings, string L3Group, long L3Bytes);

/// <summary>Processors sharing one L3 cache (an AMD CCD, or a whole Intel die): pinning a machine inside one keeps its cache warm.</summary>
public sealed record CacheGroup(string Name, IReadOnlyList<int> Cpus, long L3Bytes, int Cores)
{
    /// <summary>"8 cores, 16 threads, 96 MB L3 (the larger cache: 3D V-Cache on these processors)".</summary>
    public string Describe(bool largest) =>
        $"{Cores} cores, {Cpus.Count} threads, {L3Bytes / (1024 * 1024)} MB L3{(largest ? " (the largest cache)" : "")}";
}

/// <summary>This computer's processors from sysfs (/sys/devices/system/cpu), grouped the way pinning needs them.</summary>
public static class CpuTopology
{
    public static IReadOnlyList<HostCpu> List(string sys = "/sys/devices/system/cpu")
    {
        var cpus = new List<HostCpu>();
        if (!Directory.Exists(sys)) return cpus;
        foreach (var dir in Directory.EnumerateDirectories(sys, "cpu*"))
        {
            if (!int.TryParse(Path.GetFileName(dir)[3..], CultureInfo.InvariantCulture, out var id)) continue;
            if (Read(Path.Combine(dir, "online")) == "0") continue;
            var core = int.TryParse(Read(Path.Combine(dir, "topology", "core_id")), CultureInfo.InvariantCulture, out var c) ? c : id;
            var die = int.TryParse(Read(Path.Combine(dir, "topology", "die_id")), CultureInfo.InvariantCulture, out var d) ? d : 0;
            var siblings = ParseList(Read(Path.Combine(dir, "topology", "thread_siblings_list")) ?? id.ToString(CultureInfo.InvariantCulture));
            var l3 = Read(Path.Combine(dir, "cache", "index3", "shared_cpu_list")) ?? "all";
            cpus.Add(new HostCpu(id, core, die, siblings, l3, ParseSize(Read(Path.Combine(dir, "cache", "index3", "size")))));
        }

        return cpus.OrderBy(c => c.Id).ToList();
    }

    public static IReadOnlyList<CacheGroup> Groups(IReadOnlyList<HostCpu> cpus) =>
        cpus.GroupBy(c => c.L3Group).Select((g, i) => new CacheGroup($"Group {i + 1}", g.Select(c => c.Id).Order().ToList(), g.First().L3Bytes,
                g.Select(c => (c.Die, c.Core)).Distinct().Count())).ToList();

    /// <summary>
    /// The processors to pin <paramref name="count"/> guest processors to: inside the cache group with the most L3 (the
    /// V-Cache CCD on X3D parts), whole cores first (both threads of a core, as a guest sees hyper-threads), leaving
    /// the group's first core to this computer when the group has room. <c>null</c> when the machine is bigger than any group.
    /// </summary>
    public static List<int>? Recommend(IReadOnlyList<HostCpu> cpus, int count)
    {
        foreach (var group in Groups(cpus).OrderByDescending(g => g.L3Bytes).ThenByDescending(g => g.Cpus.Count))
        {
            var cores = cpus.Where(c => group.Cpus.Contains(c.Id)).GroupBy(c => (c.Die, c.Core)).OrderBy(g => g.Min(c => c.Id))
                .Select(g => g.Select(c => c.Id).Order().ToList()).ToList();
            if (cores.Sum(c => c.Count) - (cores.Count > 1 ? cores[0].Count : 0) >= count) cores.RemoveAt(0);
            var chosen = cores.SelectMany(c => c).Take(count).ToList();
            if (chosen.Count == count) return chosen;
        }

        return null;
    }

    /// <summary>"0-7,16-23" → 0…7, 16…23.</summary>
    public static List<int> ParseList(string text)
    {
        var list = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], CultureInfo.InvariantCulture, out var a) && int.TryParse(range[1], CultureInfo.InvariantCulture, out var b))
                list.AddRange(Enumerable.Range(a, Math.Max(0, b - a + 1)));
            else if (int.TryParse(part, CultureInfo.InvariantCulture, out var one)) list.Add(one);
        }

        return list;
    }

    /// <summary>0…7, 16…23 → "0-7,16-23".</summary>
    public static string FormatList(IEnumerable<int> cpus)
    {
        var sorted = cpus.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j > i ? $"{sorted[i]}-{sorted[j]}" : sorted[i].ToString(CultureInfo.InvariantCulture));
            i = j + 1;
        }

        return string.Join(',', parts);
    }

    private static long ParseSize(string? text)
    {
        if (text is null) return 0;
        var t = text.Trim().ToUpperInvariant();
        var mult = t.EndsWith('K') ? 1024L : t.EndsWith('M') ? 1024L * 1024 : 1;
        return long.TryParse(t.TrimEnd('K', 'M'), CultureInfo.InvariantCulture, out var n) ? n * mult : 0;
    }

    private static string? Read(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
