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

    public string SettingsFile => Path.Combine(Config, "settings.json");

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
    }
}
