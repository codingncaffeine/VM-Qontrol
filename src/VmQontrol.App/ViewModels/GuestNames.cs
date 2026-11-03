using VmQontrol.Core.Machines;

namespace VmQontrol.App.ViewModels;

/// <summary>Human names for guest profiles, and the two-letter badge the library shows (no vendor logos).</summary>
public static class GuestNames
{
    public static string Describe(GuestConfig g)
    {
        var release = g.Release.Length > 0 ? " " + Title(g.Release) : "";
        return g.Os switch
        {
            "windows" => "Windows" + release,
            "windows-server" => "Windows Server" + release,
            "macos" => "macOS" + release,
            "linux" => "Linux" + release,
            "linux_old" => "Linux (older)",
            "freebsd" => "FreeBSD" + release,
            "ghostbsd" => "GhostBSD" + release,
            "haiku" => "Haiku",
            "solaris" => "Solaris",
            "reactos" => "ReactOS",
            "freedos" => "FreeDOS",
            "kolibrios" => "KolibriOS",
            _ when g.Os.EndsWith("bsd", StringComparison.Ordinal) => Title(g.Os) + release,
            _ => Title(g.Os) + release,
        };
    }

    public static string Badge(GuestConfig g) => g.Os switch
    {
        "windows" or "windows-server" => "Wi",
        "macos" => "Ma",
        "linux" or "linux_old" => "Li",
        "freedos" => "Do",
        _ when g.Os.EndsWith("bsd", StringComparison.Ordinal) => "Bs",
        _ => g.Os.Length >= 2 ? char.ToUpperInvariant(g.Os[0]) + g.Os[1..2] : "VM",
    };

    /// <summary>The badge color family: windows, apple, linux, bsd or other.</summary>
    public static string Family(GuestConfig g) => g.Os switch
    {
        "windows" or "windows-server" or "reactos" => "windows",
        "macos" => "apple",
        "linux" or "linux_old" => "linux",
        _ when g.Os.EndsWith("bsd", StringComparison.Ordinal) => "bsd",
        _ => "other",
    };

    private static string Title(string s) => string.Join(' ', s.Split('-', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}
