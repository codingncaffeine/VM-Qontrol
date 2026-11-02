using VmQontrol.Core.Machines;

namespace VmQontrol.Core.OsInfo;

/// <summary>From an osinfo-db entry to the guest profile VM Qontrol builds the machine with (quickemu's guest names).</summary>
public static class GuestMapping
{
    public static GuestConfig ToGuest(OsEntry os)
    {
        var shortId = os.ShortId;
        var (name, release) = os.Family switch
        {
            "winnt" when shortId.StartsWith("win2k", StringComparison.Ordinal) => ("windows-server", shortId[3..]),
            "winnt" or "win9x" => ("windows", shortId.StartsWith("win", StringComparison.Ordinal) ? shortId[3..] : ""),
            "darwin" => ("macos", ""),
            "freebsd" => (os.Distro == "ghostbsd" ? "ghostbsd" : "freebsd", os.Version),
            "netbsd" or "openbsd" or "dragonfly" => (os.Family + (os.Family.EndsWith("bsd", StringComparison.Ordinal) ? "" : "bsd"), os.Version),
            "haiku" => ("haiku", os.Version),
            "solaris" => ("solaris", os.Version),
            "msdos" when os.Distro == "freedos" => ("freedos", os.Version),
            _ => ("linux", os.Version),
        };
        return new GuestConfig { Os = name, Release = release, OsInfoId = os.Id };
    }
}
