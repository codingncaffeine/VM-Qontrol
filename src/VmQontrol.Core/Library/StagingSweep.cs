namespace VmQontrol.Core.Library;

/// <summary>
/// Imports and downloads work in hidden staging folders of the library (.importing, .downloads) and clean up after
/// themselves; a crash or a power cut can leave one behind, several gigabytes of a half-unpacked appliance or ISO. At
/// start, those untouched for a week go (a download resumes from its staging folder, so a recent one stays).
/// </summary>
public static class StagingSweep
{
    public static readonly TimeSpan Age = TimeSpan.FromDays(7);

    public static IReadOnlyList<string> Sweep(IEnumerable<string> libraryRoots, DateTime? now = null)
    {
        var removed = new List<string>();
        var cutoff = (now ?? DateTime.UtcNow) - Age;
        foreach (var root in libraryRoots)
        {
            foreach (var name in new[] { ".importing", ".downloads" })
            {
                var parent = Path.Combine(root, name);
                if (!Directory.Exists(parent)) continue;
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(parent))
                    {
                        var newest = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc)
                            .Append(Directory.GetLastWriteTimeUtc(dir)).Max();
                        if (newest >= cutoff) continue;
                        Directory.Delete(dir, recursive: true);
                        removed.Add(dir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Leftovers in {parent} could not be cleared: {ex.Message}");
                }
            }
        }

        foreach (var dir in removed) Log.Info($"Cleared an interrupted import or download left in {dir}.");
        return removed;
    }
}
