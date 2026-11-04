using System.Text;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.WindowsApps;

/// <summary>
/// D43 step 20f: Windows programs in this computer's own app menu (the freedesktop Desktop Entry and Icon Theme specs, the
/// way Parallels and WinApps put Windows programs in the Dock and the menu). One entry per program in the person's
/// applications folder, its icon in their hicolor theme at 256 px, Exec = VM Qontrol's window program with
/// --windows-app, which powers the machine on when needed, asks for the password when none is kept, and opens the
/// program. Every entry is marked with the machine it belongs to, so removing the machine removes them. The folder is
/// <see cref="QontrolPaths.DesktopData"/>: an isolated profile keeps its entries to itself.
/// </summary>
public static class WindowsAppsMenu
{
    public const string Argument = "--windows-app";

    private const string MachineKey = "X-VMQontrol-Machine=";

    /// <summary>VM Qontrol's own icon (the app id; DesktopIdentity.AppId in the window program), for a program with none.</summary>
    public const string AppIconName = "io.github.codingncaffeine.VMQontrol";

    /// <summary>The entry's and the icon's name: vm-qontrol-win-MACHINE-APP.</summary>
    public static string EntryName(MachineConfig config, WindowsApp app) => $"vm-qontrol-win-{config.Id[..Math.Min(12, config.Id.Length)]}-{app.Slug}";

    public static string EntryFile(MachineConfig config, WindowsApp app, string dataHome) =>
        Path.Combine(dataHome, "applications", EntryName(config, app) + ".desktop");

    /// <summary>
    /// The window program: "vmqontrol" when that name on the PATH is this very program (a package's /usr/bin link, which
    /// survives upgrades), else its full path beside this one (a build or an unpacked tarball), else the bare name.
    /// </summary>
    public static string WindowProgram()
    {
        var here = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        var beside = new[] { "vmqontrol", "VmQontrol.App" }.Select(n => Path.Combine(here, n)).FirstOrDefault(File.Exists);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var onPath = Path.Combine(dir, "vmqontrol");
            if (beside is not null && File.Exists(onPath) && SameFile(onPath, beside)) return "vmqontrol";
        }

        return beside ?? "vmqontrol";
    }

    private static bool SameFile(string a, string b)
    {
        try
        {
            static string Real(string f) => new FileInfo(f).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(f);
            return Real(a) == Real(b);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A Desktop Entry string value: the spec's escapes for backslash and line breaks.</summary>
    private static string Value(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);

    /// <summary>An Exec argument, quoted as the spec asks.</summary>
    private static string Quote(string arg) => "\"" + arg.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal) + "\"";

    public static string Entry(MachineConfig config, WindowsApp app, string program)
    {
        // Exec arguments go through the spec's quoting and then its general string escapes (a backslash doubles again).
        static string Exec(string arg) => Quote(arg).Replace("\\", "\\\\", StringComparison.Ordinal);
        var b = new StringBuilder();
        b.Append("[Desktop Entry]\n");
        b.Append("Type=Application\n");
        b.Append("Name=").Append(Value(app.Name)).Append('\n');
        b.Append("GenericName=Windows app\n");
        b.Append("Comment=").Append(Value($"{app.Name} from Windows on {config.Name} (VM Qontrol)")).Append('\n');
        b.Append("Exec=").Append(Exec(program)).Append(' ').Append(Argument).Append(' ').Append(Exec(config.Id)).Append(' ').Append(Exec(app.Slug)).Append('\n');
        b.Append("Icon=").Append(app.Icon is null ? AppIconName : EntryName(config, app)).Append('\n');
        b.Append("Terminal=false\n");
        b.Append("Categories=Utility;\n");
        b.Append("Keywords=Windows;").Append(Value(config.Name).Replace(";", " ", StringComparison.Ordinal)).Append(";\n");
        // Every window of a machine's apps carries the machine's class (one client per machine), so the dock groups them.
        b.Append("StartupWMClass=").Append(WindowsAppsClient.WindowClass(config)).Append('\n');
        b.Append(MachineKey).Append(config.Id).Append('\n');
        return b.ToString();
    }

    /// <summary>Adds (or refreshes) the program's entry and icon.</summary>
    public static void Add(MachineConfig config, string machineDirectory, WindowsApp app, string dataHome, string? program = null)
    {
        var home = dataHome;
        if (app.Icon is { } icon && File.Exists(Path.Combine(WindowsAppsCatalog.HostFolder(machineDirectory), icon)))
        {
            var target = Path.Combine(home, "icons", "hicolor", "256x256", "apps", EntryName(config, app) + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(WindowsAppsCatalog.HostFolder(machineDirectory), icon), target, overwrite: true);
        }

        var file = EntryFile(config, app, home);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".tmp", Entry(config, app, program ?? WindowProgram()));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    public static void Remove(MachineConfig config, WindowsApp app, string dataHome)
    {
        var home = dataHome;
        File.Delete(EntryFile(config, app, home));
        File.Delete(Path.Combine(home, "icons", "hicolor", "256x256", "apps", EntryName(config, app) + ".png"));
    }

    /// <summary>Whether the program has an entry in this computer's app menu.</summary>
    public static bool Has(MachineConfig config, WindowsApp app, string dataHome) => File.Exists(EntryFile(config, app, dataHome));

    /// <summary>Removes every entry (and icon) of one machine: those that name it, by name, one at a time.</summary>
    public static int RemoveAll(string machineId, string dataHome)
    {
        var home = dataHome;
        var applications = Path.Combine(home, "applications");
        if (!Directory.Exists(applications)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(applications, "vm-qontrol-win-*.desktop"))
        {
            try
            {
                if (!File.ReadLines(file).Any(l => l == MachineKey + machineId)) continue;
                File.Delete(file);
                File.Delete(Path.Combine(home, "icons", "hicolor", "256x256", "apps", Path.GetFileNameWithoutExtension(file) + ".png"));
                removed++;
            }
            catch (IOException ex)
            {
                Log.Warn($"A Windows app's menu entry could not be removed ({file}: {ex.Message}).");
            }
        }

        return removed;
    }
}
