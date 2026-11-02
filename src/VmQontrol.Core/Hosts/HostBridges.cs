namespace VmQontrol.Core.Hosts;

/// <summary>A network bridge on this computer, and whether QEMU's bridge helper lets machines join it.</summary>
public sealed record HostBridge(string Name, bool Allowed, bool Up);

/// <summary>This computer's bridges (sysfs) and the ones /etc/qemu/bridge.conf allows ("allow br0", "allow all", include lines).</summary>
public static class HostBridges
{
    public static IReadOnlyList<HostBridge> List(string sys = "/sys", string conf = "/etc/qemu/bridge.conf")
    {
        var allowed = Allowed(conf, 0);
        var root = Path.Combine(sys, "class", "net");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).OfType<string>()
            .Where(n => Directory.Exists(Path.Combine(root, n, "bridge"))).Order(StringComparer.Ordinal)
            .Select(n => new HostBridge(n, allowed.Contains("all") || allowed.Contains(n), Read(Path.Combine(root, n, "operstate")) is "up" or "unknown"))
            .ToList();
    }

    /// <summary>The names bridge.conf allows, following its include lines (as qemu-bridge-helper does, one level is enough).</summary>
    public static HashSet<string> Allowed(string conf, int depth)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        string[] lines;
        try
        {
            lines = File.ReadAllLines(conf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return names;
        }

        foreach (var raw in lines)
        {
            var line = raw.Split('#')[0].Trim();
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (parts[0] == "allow") names.Add(parts[1]);
            else if (parts[0] == "include" && depth < 2) names.UnionWith(Allowed(parts[1], depth + 1));
        }

        return names;
    }

    private static string? Read(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
