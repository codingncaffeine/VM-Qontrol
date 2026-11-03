using System.Globalization;

namespace VmQontrol.Core.Hosts;

/// <summary>One size of this computer's huge page pool: how many are reserved and how many are free now.</summary>
public sealed record HugePageSize(string Label, long PageBytes, long Total, long Free)
{
    public long FreeBytes => Free * PageBytes;

    /// <summary>Pages of this size a machine of <paramref name="memoryMiB"/> needs.</summary>
    public long PagesFor(long memoryMiB) => (memoryMiB * 1024 * 1024 + PageBytes - 1) / PageBytes;

    public string SysfsFile(string root = "/sys/kernel/mm/hugepages") => Path.Combine(root, $"hugepages-{PageBytes / 1024}kB", "nr_hugepages");
}

/// <summary>
/// The huge page pool (/sys/kernel/mm/hugepages), and reserving pages in it through polkit (pkexec tee on the kernel's own
/// file, as the documentation describes). A reservation lasts until the computer restarts; 1 GB pages may not be found
/// once memory is fragmented, which the result says.
/// </summary>
public static class HugePagePool
{
    public static IReadOnlyList<HugePageSize> Sizes(string root = "/sys/kernel/mm/hugepages")
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root, "hugepages-*kB").Select(dir =>
        {
            var kb = long.Parse(Path.GetFileName(dir)["hugepages-".Length..^2], CultureInfo.InvariantCulture);
            long Read(string f) => long.TryParse(File.ReadAllText(Path.Combine(dir, f)).Trim(), CultureInfo.InvariantCulture, out var n) ? n : 0;
            return new HugePageSize(kb >= 1024 * 1024 ? $"{kb / 1024 / 1024}G" : $"{kb / 1024}M", kb * 1024, Read("nr_hugepages"), Read("free_hugepages"));
        }).OrderBy(s => s.PageBytes).ToList();
    }

    /// <summary>Reserves <paramref name="pages"/> pages in all (polkit asks for the password); returns the pages now reserved, or why not.</summary>
    public static async Task<(long Reserved, string? Problem)> ReserveAsync(HugePageSize size, long pages, CancellationToken ct = default)
    {
        if (!File.Exists("/usr/bin/pkexec")) return (size.Total, "polkit's pkexec is not installed, so the password cannot be asked for");
        var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/pkexec") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in (string[])["/usr/bin/tee", size.SysfsFile()]) start.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(start)!;
        await p.StandardInput.WriteLineAsync(pages.ToString(CultureInfo.InvariantCulture).AsMemory(), ct).ConfigureAwait(false);
        p.StandardInput.Close();
        var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var now = Sizes().FirstOrDefault(s => s.PageBytes == size.PageBytes)?.Total ?? 0;
        if (p.ExitCode is 126 or 127) return (now, p.ExitCode == 126 ? "the password prompt was closed" : "polkit refused");
        if (p.ExitCode != 0) return (now, error.Trim());
        return now < pages ? (now, $"the kernel found room for {now} of {pages} pages (memory is fragmented; reserve right after the computer starts, or use 2 MB pages)") : (now, null);
    }
}
