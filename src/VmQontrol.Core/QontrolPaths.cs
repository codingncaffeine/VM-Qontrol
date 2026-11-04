namespace VmQontrol.Core;

/// <summary>
/// Where VM Qontrol keeps its own files: settings, caches and logs in the XDG folders, machines in the library roots.
/// <c>VMQONTROL_HOME=DIR</c> puts every one of them under DIR, library included, so a test or a trial run never touches
/// the real profile or the real machines.
/// </summary>
public sealed record QontrolPaths(string Config, string Data, string State, string Cache, string DefaultLibrary, bool Isolated)
{
    public const string HomeVariable = "VMQONTROL_HOME";

    public string Logs => Path.Combine(State, "logs");

    /// <summary>
    /// Where this computer's app menu entries and their icons go (the XDG data home), or, for an isolated profile (a
    /// trial or a test), a folder inside it: such a profile never changes the desktop.
    /// </summary>
    public string DesktopData => Isolated
        ? Path.Combine(Data, "desktop")
        : Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } h ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    public string SettingsFile => Path.Combine(Config, "settings.json");

    /// <summary>
    /// Where running machines keep their sockets: the user's runtime folder (short, private, cleared at log out), because a
    /// unix socket path may not exceed 107 bytes. An isolated profile gets a folder of its own beside the real one.
    /// </summary>
    public string Runtime
    {
        get
        {
            var baseDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r ? r : Path.Combine(Path.GetTempPath(), $"vmqontrol-{Environment.UserName}");
            if (!Isolated) return Path.Combine(baseDir, "vmqontrol");
            var tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Config)))[..8].ToLowerInvariant();
            return Path.Combine(baseDir, $"vmqontrol-{tag}");
        }
    }

    /// <summary>A machine's runtime folder, named by the start of its id.</summary>
    public string RuntimeFor(string machineId) => Path.Combine(Runtime, machineId[..Math.Min(12, machineId.Length)]);

    public static QontrolPaths ForCurrentUser() => FromEnvironment(Environment.GetEnvironmentVariable);

    public static QontrolPaths FromEnvironment(Func<string, string?> env)
    {
        if (env(HomeVariable) is { Length: > 0 } home)
        {
            home = Path.GetFullPath(home);
            return new QontrolPaths(
                Path.Combine(home, "config"), Path.Combine(home, "data"), Path.Combine(home, "state"),
                Path.Combine(home, "cache"), Path.Combine(home, "VMs"), Isolated: true);
        }

        var user = env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Xdg(string variable, string fallback) => env(variable) is { Length: > 0 } v && Path.IsPathRooted(v) ? v : Path.Combine(user, fallback);
        const string app = "vmqontrol";
        return new QontrolPaths(
            Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), app),
            Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")), app),
            Path.Combine(Xdg("XDG_STATE_HOME", Path.Combine(".local", "state")), app),
            Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), app),
            // quickemu's folder: machines made with quickemu are found on the first run.
            Path.Combine(user, "VMs"),
            Isolated: false);
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Config, Data, State, Cache, Logs }) Directory.CreateDirectory(dir);
        EnsurePrivateRuntime();
    }

    /// <summary>
    /// Without XDG_RUNTIME_DIR the runtime folder is in the shared temporary folder, where another account could make it
    /// first and watch or replace the machines' sockets: it must be this account's own, not a link, and closed to others.
    /// </summary>
    public void EnsurePrivateRuntime()
    {
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 }) return;
        var shared = Path.Combine(Path.GetTempPath(), $"vmqontrol-{Environment.UserName}");
        var info = new DirectoryInfo(shared);
        if (!info.Exists) Directory.CreateDirectory(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        info.Refresh();
        if (info.LinkTarget is not null || OwnerOf(shared) != NativeLibc.Geteuid())
            throw new IOException($"{shared} belongs to another account or is a link, so VM Qontrol will not keep its machines' sockets there. Remove it, or sign in with a session that sets XDG_RUNTIME_DIR.");
        File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static uint OwnerOf(string path) => NativeLibc.Stat(path, out var owner) == 0 ? owner : uint.MaxValue;
}
