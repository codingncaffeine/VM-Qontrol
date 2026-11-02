using VmQontrol.Core;

namespace VmQontrol.App.Platform;

/// <summary>
/// Registers VM Qontrol with the desktop when it runs without a package (from a build or an unpacked tarball): the
/// taskbar and the launcher find an application's name and icon through a desktop entry named after its app id, and a
/// package installs that entry, not a build folder. So, when no entry is installed anywhere on the data path, the
/// packaged entry is written to the person's own applications folder with Exec pointing at the running program, and
/// the icons to their hicolor theme. An entry a package or the person installed is never written over: only one this
/// method wrote (marked <c>X-VMQontrol-Generated</c>) is updated, when the program has moved.
/// </summary>
internal static class DesktopIntegration
{
    private const string Marker = "X-VMQontrol-Generated=true";

    /// <summary>Checks and, when needed, writes the entry and icons. Runs on a worker; never throws.</summary>
    public static void Ensure(string? dataHome = null, IReadOnlyList<string>? dataDirs = null, string? program = null)
    {
        try
        {
            program ??= Environment.ProcessPath;
            if (program is null || !OperatingSystem.IsLinux()) return;
            dataHome ??= Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } h ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            dataDirs ??= (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share").Split(':', StringSplitOptions.RemoveEmptyEntries);
            var name = DesktopIdentity.AppId + ".desktop";

            // A package (or the person) installed it: leave it alone.
            if (dataDirs.Any(dir => File.Exists(Path.Combine(dir, "applications", name)))) return;
            var target = Path.Combine(dataHome, "applications", name);
            if (File.Exists(target) && !File.ReadAllText(target).Contains(Marker, StringComparison.Ordinal)) return;

            var entry = Entry(program);
            WriteIcons(dataHome);
            if (File.Exists(target) && File.ReadAllText(target) == entry) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".tmp";
            File.WriteAllText(temp, entry);
            File.Move(temp, target, overwrite: true);
            Log.Info($"Registered with the desktop: {target} (runs {program})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not register with the desktop ({ex.Message}); the taskbar may show a generic icon.");
        }
    }

    /// <summary>The packaged entry with Exec set to this program (quoted per the Desktop Entry spec) and the marker.</summary>
    internal static string Entry(string program)
    {
        var lines = Resource("desktop-entry") is { } stream ? new StreamReader(stream).ReadToEnd().Split('\n') : throw new IOException("the desktop entry is not in the build");
        var exec = "\"" + program.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
        var result = lines.Select(l => l.StartsWith("Exec=", StringComparison.Ordinal) ? $"Exec={exec} %U" : l).Where(l => l.Length > 0).ToList();
        result.Add("TryExec=" + program);
        result.Add(Marker);
        return string.Join('\n', result) + "\n";
    }

    private static void WriteIcons(string dataHome)
    {
        var assembly = typeof(DesktopIntegration).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith("icon/", StringComparison.Ordinal)))
        {
            // "icon/48x48/apps/" (MSBuild's RecursiveDir, either slash).
            var size = resource["icon/".Length..].Replace('\\', '/').Split('/')[0];
            var file = Path.Combine(dataHome, "icons", "hicolor", size, "apps", DesktopIdentity.AppId + ".png");
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if (File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(buffer.ToArray())) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, buffer.ToArray());
        }
    }

    private static Stream? Resource(string name) => typeof(DesktopIntegration).Assembly.GetManifestResourceStream(name);
}
