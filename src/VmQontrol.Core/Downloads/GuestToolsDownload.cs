using System.Diagnostics;
using VmQontrol.Core.Media;

namespace VmQontrol.Core.Downloads;

/// <summary>
/// The Windows guest tools (D31): the virtio-win project's stable VirtIO driver disc, downloaded once into the app's data
/// folder and shared by every machine. HTTPS only (the project's redirects point at plain HTTP; they are followed as
/// HTTPS), resumed after a dropped connection, and kept only when it IS the driver disc: the size the server named, the
/// volume label "virtio-win-…" and virtio-win-guest-tools.exe on it. The project publishes no checksum for the disc itself.
/// </summary>
public static class GuestToolsDownload
{
    public const string StableUrl = "https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso";

    public const string FileName = "virtio-win.iso";

    /// <summary>Roughly, for the button before the size is known.</summary>
    public const string ApproximateSize = "about 900 MB";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };

    public static string Target(QontrolPaths paths) => Path.Combine(paths.Data, "guest-tools", FileName);

    /// <summary>The downloaded disc when it is there and whole.</summary>
    public static string? Existing(QontrolPaths paths) => Target(paths) is var t && IsDriverDisc(t) ? t : null;

    /// <summary>Whether <paramref name="iso"/> is the VirtIO driver disc (label and the guest tools installer on it).</summary>
    public static bool IsDriverDisc(string iso)
    {
        if (!File.Exists(iso)) return false;
        try
        {
            if (IsoVolume.Read(iso) is not { } volume || !volume.VolumeId.StartsWith("virtio-win", StringComparison.OrdinalIgnoreCase)) return false;
            using var files = IsoFiles.Open(iso);
            return files?.Exists("virtio-win-guest-tools.exe") == true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The final address of <paramref name="url"/>, every hop made HTTPS.</summary>
    public static async Task<Uri> ResolveAsync(string url, CancellationToken ct)
    {
        var uri = Secure(new Uri(url));
        for (var hop = 0; hop < 8; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                uri = Secure(next.IsAbsoluteUri ? next : new Uri(uri, next));
                continue;
            }

            response.EnsureSuccessStatusCode();
            return uri;
        }

        throw new DownloadFailedException(["The guest tools' address redirected too many times."]);
    }

    /// <summary>Plain HTTP is never used on the internet; this computer (a test's own server) is the only exception.</summary>
    private static Uri Secure(Uri uri) => uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback ? new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri : uri;

    /// <summary>Downloads the disc (or returns the one already here). Call on a worker.</summary>
    public static async Task<string> GetAsync(QontrolPaths paths, IProgress<DownloadProgress> progress, CancellationToken ct, string url = StableUrl)
    {
        var target = Target(paths);
        if (IsDriverDisc(target)) return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";
        progress.Report(new DownloadProgress(0, null, "Finding the newest guest tools…"));
        Uri source;
        try
        {
            source = await ResolveAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadFailedException([$"The guest tools could not be reached ({ex.Message}). Check the connection and try again."]);
        }

        long? size = null;
        for (var attempt = 1; ; attempt++)
        {
            var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); continue; }
                response.EnsureSuccessStatusCode();
                if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0;
                size = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength is { } len ? len + have : null);
                await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = DownloadWrite.Open(partial, append: have > 0))
                {
                    var buffer = new byte[1 << 20];
                    var last = Stopwatch.StartNew();
                    int read;
                    while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await DownloadWrite.WriteAsync(output, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        have += read;
                        if (last.ElapsedMilliseconds >= 250)
                        {
                            progress.Report(new DownloadProgress(have, size, "Downloading the guest tools…"));
                            last.Restart();
                        }
                    }
                }

                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < 4 && !ct.IsCancellationRequested)
            {
                progress.Report(new DownloadProgress(have, size, $"The connection dropped; resuming ({attempt} of 3)…"));
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new DownloadFailedException([$"The guest tools did not download ({ex.Message}). Try again; what arrived is kept and the download carries on from there."]);
            }
        }

        progress.Report(new DownloadProgress(size ?? 0, size, "Checking the disc…"));
        var got = new FileInfo(partial).Length;
        if (size is { } expected && got != expected)
            throw new DownloadFailedException([$"The download stopped short ({got} of {expected} bytes). Try again; it carries on from there."]);
        if (!IsDriverDisc(partial))
        {
            File.Delete(partial);
            throw new DownloadFailedException(["What arrived is not the VirtIO driver disc (the server may have sent a web page); it was deleted. Try again later."]);
        }

        File.Move(partial, target, overwrite: true);
        Log.Info($"Guest tools downloaded: {source} → {target} ({got} bytes).");
        return target;
    }
}
