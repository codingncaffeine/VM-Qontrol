using System.Net;
using VmQontrol.Core.Downloads;
using VmQontrol.Core.Media;

namespace VmQontrol.Tests;

/// <summary>The guest tools download against a server on this computer: redirects followed, the disc checked, a web page refused.</summary>
public sealed class GuestToolsDownloadTests
{
    private static byte[] DriverDisc(TestProfile profile, string label = "virtio-win-0.1.302")
    {
        var iso = Path.Combine(profile.Root, $"src-{Guid.NewGuid():N}.iso");
        IsoWriter.Write(iso, label, new Dictionary<string, byte[]> { ["virtio-win-guest-tools.exe"] = "MZ"u8.ToArray() }, keepLabelCase: true);
        return File.ReadAllBytes(iso);
    }

    /// <summary>Serves /stable (a redirect, as fedorapeople's) and /disc.iso with <paramref name="body"/>.</summary>
    private static (HttpListener Listener, string Url) Serve(byte[] body)
    {
        var port = Random.Shared.Next(20000, 60000);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
                if (ctx.Request.Url!.AbsolutePath == "/stable")
                {
                    ctx.Response.StatusCode = 301;
                    ctx.Response.RedirectLocation = $"http://127.0.0.1:{port}/disc.iso";
                }
                else
                {
                    ctx.Response.ContentLength64 = body.Length;
                    if (ctx.Request.HttpMethod != "HEAD") await ctx.Response.OutputStream.WriteAsync(body);
                }

                ctx.Response.Close();
            }
        });
        return (listener, $"http://127.0.0.1:{port}/stable");
    }

    [Fact]
    public async Task The_disc_is_downloaded_through_the_redirect_checked_and_kept_for_next_time()
    {
        using var profile = new TestProfile("tools-dl");
        var (listener, url) = Serve(DriverDisc(profile));
        using var _ = listener;
        var reports = new List<DownloadProgress>();
        var path = await GuestToolsDownload.GetAsync(profile.Paths, new SyncProgress(reports.Add), TestContext.Current.CancellationToken, url);
        Assert.Equal(GuestToolsDownload.Target(profile.Paths), path);
        Assert.True(GuestToolsDownload.IsDriverDisc(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.Contains(reports, r => r.Status.StartsWith("Checking", StringComparison.Ordinal));
        Assert.Equal(path, GuestToolsDownload.Existing(profile.Paths));
        listener.Stop();
        // Already here: no server needed.
        Assert.Equal(path, await GuestToolsDownload.GetAsync(profile.Paths, new SyncProgress(_ => { }), TestContext.Current.CancellationToken, url));
    }

    [Fact]
    public async Task A_web_page_or_another_disc_is_refused_and_deleted()
    {
        using var profile = new TestProfile("tools-dl-bad");
        foreach (var body in new[] { "<html>Please verify you are human</html>"u8.ToArray(), DriverDisc(profile, "UBUNTU") })
        {
            var (listener, url) = Serve(body);
            using var _ = listener;
            var ex = await Assert.ThrowsAsync<DownloadFailedException>(() => GuestToolsDownload.GetAsync(profile.Paths, new SyncProgress(_ => { }), TestContext.Current.CancellationToken, url));
            Assert.Contains("not the VirtIO driver disc", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(GuestToolsDownload.Target(profile.Paths)));
            Assert.False(File.Exists(GuestToolsDownload.Target(profile.Paths) + ".part"));
        }
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
