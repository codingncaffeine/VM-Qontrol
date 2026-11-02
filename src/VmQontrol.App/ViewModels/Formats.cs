using System.Globalization;

namespace VmQontrol.App.ViewModels;

internal static class Formats
{
    public static string Bytes(double bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (1L << 20)).ToString(bytes >= 100L << 20 ? "0" : "0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1L << 10 => (bytes / (1L << 10)).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString("0", CultureInfo.CurrentCulture) + " B",
    };

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Percent(double p) => p.ToString("0", CultureInfo.CurrentCulture) + "%";
}
