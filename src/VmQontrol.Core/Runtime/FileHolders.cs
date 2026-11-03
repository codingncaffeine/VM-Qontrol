using System.Text.RegularExpressions;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// Who holds a disk QEMU could not lock ("Failed to get "write" lock / Is another process using the image [PATH]?"): the
/// processes with that file open, read from /proc, named in words (another machine by its name, anything else by its
/// program).
/// </summary>
public static partial class FileHolders
{
    /// <summary>The locked file named in QEMU's message, or <c>null</c>.</summary>
    public static string? LockedFile(string qemuError) => LockedImage().Match(qemuError) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>A sentence naming who holds the file QEMU could not lock, or <c>null</c> when the message is not about a lock.</summary>
    public static string? Explain(string qemuError, string procRoot = "/proc")
    {
        if (LockedFile(qemuError) is not { } file) return null;
        var holders = Holders(file, procRoot).Distinct().ToList();
        var what = holders.Count > 0 ? string.Join(", ", holders) : "another program";
        return $"{Path.GetFileName(file)} is in use by {what}. Close that, then power on again.";
    }

    /// <summary>Each process with the file open, described: "the virtual machine NAME (process N)" or "PROGRAM (process N)".</summary>
    public static IEnumerable<string> Holders(string file, string procRoot = "/proc")
    {
        var target = Path.GetFullPath(file);
        foreach (var dir in SafeDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
            var open = false;
            foreach (var fd in SafeFiles(Path.Combine(dir, "fd")))
            {
                try
                {
                    if (new FileInfo(fd).LinkTarget is { } link && link == target) { open = true; break; }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            if (!open) continue;
            string[] args;
            try
            {
                args = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }
            catch (IOException)
            {
                continue;
            }

            var name = Array.IndexOf(args, "-name") is var i and >= 0 && i + 1 < args.Length ? args[i + 1].Split(",process=")[0].Split(",debug-threads")[0] : null;
            yield return name is not null ? $"the virtual machine \"{name}\" (process {pid})" : $"{Path.GetFileName(args.FirstOrDefault() ?? "a program")} (process {pid})";
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    [GeneratedRegex(@"Is another process using the image \[(.+?)\]\?")]
    private static partial Regex LockedImage();
}
