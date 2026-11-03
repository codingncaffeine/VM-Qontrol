namespace VmQontrol.Core.Hosts;

/// <summary>
/// Opening VM Qontrol at sign-in: an XDG autostart entry (~/.config/autostart), which GNOME, KDE Plasma, Xfce and the
/// rest follow, and which systemd's xdg-autostart generator turns into a user unit where the session uses systemd.
/// </summary>
public static class Autostart
{
    public const string FileName = "io.github.codingncaffeine.VMQontrol.desktop";

    /// <summary>The argument the entry passes, so the app knows it was started at sign-in.</summary>
    public const string Argument = "--autostart";

    public static string Path(string? configHome = null)
    {
        var config = configHome
                     ?? (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
        return System.IO.Path.Combine(config, "autostart", FileName);
    }

    public static bool IsOn(string? configHome = null) => File.Exists(Path(configHome));

    /// <summary>Writes or removes the entry; <paramref name="program"/> is this app's executable.</summary>
    public static void Set(bool on, string program, string? configHome = null)
    {
        var file = Path(configHome);
        if (!on)
        {
            File.Delete(file);
            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"""
            [Desktop Entry]
            Type=Application
            Name=VM Qontrol
            Comment=Open VM Qontrol when you sign in
            Exec="{program}" {Argument}
            Icon=io.github.codingncaffeine.VMQontrol
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }
}
