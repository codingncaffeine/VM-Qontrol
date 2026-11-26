using System.Security.Cryptography;
using System.Text.Json;

namespace VmQontrol.Core.Downloads;

/// <summary>
/// Omarchy's installer disc, straight from iso.omarchy.org (quickget does not list it). The version is the latest
/// release of omacom/omarchy, as Omarchy's own instructions find it; the disc must match the .sha256 beside it and
/// carry a good signature from Omarchy's release key (pinned in Keys/omarchy.asc), or it is deleted.
/// </summary>
public static class OmarchyDownload
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Where the version and the disc come from; tests point these at a local server.</summary>
    internal static string ReleasesUrl { get; set; } = "https://api.github.com/repos/omacom/omarchy/releases/latest";

    internal static string IsoHost { get; set; } = "https://iso.omarchy.org/";

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VM-Qontrol");
        return http;
    }

    /// <summary>The catalog's Omarchy entry (one release: the latest, found when the download starts).</summary>
    public static CatalogSystem CatalogSystem { get; } = new("omarchy", "Omarchy", "",
        [new CatalogEntry("Omarchy", "omarchy", "latest", "", "", Direct: true)]);

    /// <summary>The latest release's version, without its "v".</summary>
    public static async Task<string> LatestVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(ReleasesUrl, ct).ConfigureAwait(false));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var version = tag.TrimStart('v');
            if (version.Length == 0 || version.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
                throw new DownloadFailedException([$"Omarchy's latest release has an unexpected name ({tag})."]);
            return version;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DownloadFailedException([$"Omarchy's latest version could not be found ({ex.Message}). Check the connection and try again."]);
        }
    }

    /// <summary>Downloads and checks the disc into the library's .discs folder and returns its path (kept for the next machine).</summary>
    public static async Task<string> GetDiscAsync(string libraryRoot, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        progress.Report(new DownloadProgress(0, null, "Finding Omarchy's latest version…"));
        var version = await LatestVersionAsync(ct).ConfigureAwait(false);
        var name = $"omarchy-{version}-x86.iso";
        var discs = Path.Combine(libraryRoot, ".discs");
        Directory.CreateDirectory(discs);
        var target = Path.Combine(discs, name);

        string sums;
        byte[] signature;
        try
        {
            sums = await Http.GetStringAsync(IsoHost + name + ".sha256", ct).ConfigureAwait(false);
            signature = await Http.GetByteArrayAsync(IsoHost + name + ".sig", ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadFailedException([$"Omarchy {version}'s checksum and signature could not be downloaded ({ex.Message})."]);
        }

        var expected = Signatures.HashFor(sums, name) ?? throw new DownloadFailedException([$"Omarchy's checksum file does not list {name}, so the disc was not downloaded."]);

        // A disc already here from an earlier machine is used again once it still checks out.
        if (File.Exists(target) && await Sha256Async(target, ct).ConfigureAwait(false) == expected.ToUpperInvariant()) return target;

        var partial = target + ".part";
        var size = await ResumableDownload.ToPartialAsync(Http, new Uri(IsoHost + name), partial, progress, $"Downloading Omarchy {version}…", $"Omarchy {version}", ct).ConfigureAwait(false);
        progress.Report(new DownloadProgress(size ?? 0, size, "Checking the download against Omarchy's checksum…"));
        if (await Sha256Async(partial, ct).ConfigureAwait(false) != expected.ToUpperInvariant())
        {
            File.Delete(partial);
            throw new DownloadFailedException(["The download does not match the checksum Omarchy publishes, so it was deleted. Try again; if it happens again, the server or the connection is damaging it."]);
        }

        progress.Report(new DownloadProgress(size ?? 0, size, "Checking Omarchy's signature…"));
        try
        {
            await Signatures.VerifyDetachedAsync(Signatures.Omarchy, signature, partial, $"Omarchy {version}'s disc", ct).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    private static async Task<string> Sha256Async(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }
}
