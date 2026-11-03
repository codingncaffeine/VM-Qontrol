using System.Diagnostics;

namespace VmQontrol.Core.Downloads;

/// <summary>
/// One file over HTTP into a partial file that survives a dropped connection: three retries, each carrying on from the
/// bytes already there, and a disk-side failure named as the disk's (<see cref="DownloadWrite"/>). Returns the size the
/// server gave, and fails when fewer bytes arrived.
/// </summary>
internal static class ResumableDownload
{
    public static async Task<long?> ToPartialAsync(HttpClient http, Uri source, string partial, IProgress<DownloadProgress> progress, string status, string what, CancellationToken ct)
    {
        long? size = null;
        for (var attempt = 1; ; attempt++)
        {
            var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
                            progress.Report(new DownloadProgress(have, size, status));
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
                throw new DownloadFailedException([$"{what} did not download ({ex.Message}). Try again; what arrived is kept and the download carries on from there."]);
            }
        }

        var got = new FileInfo(partial).Length;
        if (size is { } expected && got != expected)
            throw new DownloadFailedException([$"The download stopped short ({got} of {expected} bytes). Try again; it carries on from there."]);
        return size;
    }
}
