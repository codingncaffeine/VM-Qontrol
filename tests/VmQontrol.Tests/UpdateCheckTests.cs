using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VmQontrol.App.ViewModels;
using VmQontrol.App.Views;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;

namespace VmQontrol.Tests;

/// <summary>
/// Preferences → About (s11): this version against the newest release, asked of GitHub only on Check for updates.
/// One class, so the endpoint (process-wide) is never changed by two tests at once; a local server stands in for GitHub.
/// </summary>
public sealed class UpdateCheckTests
{
    /// <summary>A stand-in for GitHub's latest-release address: path → (status, body).</summary>
    private sealed class FakeGitHub : IDisposable
    {
        private readonly HttpListener _listener = new();

        public FakeGitHub(Dictionary<string, (int Status, string Body)> answers)
        {
            // A free port: the system picks one for a listener that is let go at once.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Base = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Base);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); } catch (Exception) { return; }
                    var (status, body) = answers.TryGetValue(c.Request.Url!.AbsolutePath.Trim('/'), out var a) ? a : (404, "{}");
                    c.Response.StatusCode = status;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    await c.Response.OutputStream.WriteAsync(bytes);
                    c.Response.Close();
                }
            });
        }

        public string Base { get; }

        public void Dispose() => _listener.Close();
    }

    private static string Release(string tag) => $$"""{ "tag_name": "{{tag}}", "html_url": "https://github.com/codingncaffeine/VM-Qontrol/releases/tag/{{tag}}", "published_at": "2026-11-20T10:00:00Z" }""";

    [Fact]
    public async Task The_newest_release_is_read_and_compared_and_failures_say_why()
    {
        var ct = TestContext.Current.CancellationToken;
        using var github = new FakeGitHub(new()
        {
            ["newer"] = (200, Release("v9.9.9")),
            ["same"] = (200, Release("v" + UpdateCheck.ThisVersion)),
            ["limited"] = (403, "{}"),
            ["garbage"] = (200, "<html>"),
        });
        var before = UpdateCheck.Endpoint;
        try
        {
            UpdateCheck.Endpoint = github.Base + "newer";
            var newer = await UpdateCheck.LatestAsync(ct);
            Assert.Equal("9.9.9", newer.Version);
            Assert.EndsWith("/tag/v9.9.9", newer.Url, StringComparison.Ordinal);
            Assert.True(UpdateCheck.IsNewer(newer.Version, UpdateCheck.ThisVersion));

            // Control: the same version is not "newer".
            UpdateCheck.Endpoint = github.Base + "same";
            Assert.False(UpdateCheck.IsNewer((await UpdateCheck.LatestAsync(ct)).Version, UpdateCheck.ThisVersion));

            UpdateCheck.Endpoint = github.Base + "missing";
            Assert.Contains("no release", (await Assert.ThrowsAsync<IOException>(() => UpdateCheck.LatestAsync(ct))).Message);
            UpdateCheck.Endpoint = github.Base + "limited";
            Assert.Contains("limiting", (await Assert.ThrowsAsync<IOException>(() => UpdateCheck.LatestAsync(ct))).Message);
            UpdateCheck.Endpoint = github.Base + "garbage";
            Assert.Contains("could not be read", (await Assert.ThrowsAsync<IOException>(() => UpdateCheck.LatestAsync(ct))).Message);
        }
        finally
        {
            UpdateCheck.Endpoint = before;
        }

        Assert.Equal("0.0.6", UpdateCheck.Normalize("v0.0.6"));
        Assert.Equal("0.0.5", UpdateCheck.Normalize("0.0.5+c776d83"));
        Assert.Null(UpdateCheck.Normalize("nightly"));
        Assert.True(UpdateCheck.IsNewer("0.0.10", "0.0.9")); // numbers, not text
        Assert.False(UpdateCheck.IsNewer("nightly", "0.0.5"));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task About_shows_this_version_and_the_newest_release_after_Check_for_updates()
    {
        using var profile = new TestProfile("about");
        using var github = new FakeGitHub(new() { ["latest"] = (200, Release("v9.9.9")) });
        var before = UpdateCheck.Endpoint;
        UpdateCheck.Endpoint = github.Base + "latest";
        try
        {
            var seen = await Ui.Run(() =>
            {
                var shell = new ShellViewModel(profile.Paths, AppSettings.Load(profile.Paths)) { WatchUsb = false };
                var window = new MainWindow { DataContext = shell, Width = 1320, Height = 900 };
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                window.Show();
                _ = shell.StartAsync();
                Ui.Pump(0.4);
                // The person's path: Ctrl+K, "about", Enter → Preferences, scrolled to About.
                window.KeyPress(Avalonia.Input.Key.K, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.K, "k");
                Ui.PumpUntil(() => shell.Palette.IsOpen, "the command palette");
                window.KeyTextInput("about");
                Ui.Pump(0.2);
                var highlighted = shell.Palette.Highlighted?.Title;
                window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
                Ui.PumpUntil(() => shell.ShowPreferences, "the preferences page");
                Ui.Pump(0.5);
                var view = window.GetVisualDescendants().OfType<PreferencesView>().Single();
                var about = view.GetLogicalDescendants().OfType<Border>().First(b => b.Name == "About");
                // Scrolled to it: the card's top is inside the page's scroll viewport.
                var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
                var top = about.TranslatePoint(new Avalonia.Point(0, 0), scroller)?.Y ?? -1;
                var aboutInView = top >= 0 && top < scroller.Viewport.Height;
                string Text(string name) => view.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == name).Text ?? "";
                Button Named(string name) => view.GetLogicalDescendants().OfType<Button>().First(b => b.Name == name);
                var version = Text("VersionLine");
                var notChecked = Text("LatestText");
                var releaseShownBefore = Named("OpenRelease").IsEffectivelyVisible;
                Ui.Capture(window, "s11", "about-dark-unchecked");
                Ui.Click(window, Named("CheckUpdates"));
                Ui.PumpUntil(() => Text("LatestText").Contains("9.9.9", StringComparison.Ordinal), "the newest release shown");
                Ui.Pump(0.3);
                var found = Text("LatestText");
                var releaseShown = Named("OpenRelease").IsEffectivelyVisible;
                Ui.Capture(window, "s11", "about-dark-update");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                Ui.Pump(0.3);
                Ui.Capture(window, "s11", "about-light-update");
                Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                window.Close();
                return (version, notChecked, releaseShownBefore, found, releaseShown, highlighted, aboutInView, top);
            });

            Assert.Equal("About VM Qontrol", seen.highlighted);
            Assert.True(seen.aboutInView, $"About's top at {seen.top}");
            Assert.StartsWith($"Version {UpdateCheck.ThisVersion}", seen.version, StringComparison.Ordinal);
            Assert.Contains("not checked yet", seen.notChecked, StringComparison.Ordinal);
            Assert.False(seen.releaseShownBefore);
            Assert.Contains("Version 9.9.9 is available", seen.found, StringComparison.Ordinal);
            Assert.True(seen.releaseShown);
            AppSettings? saved = null;
            for (var i = 0; i < 50 && (saved = AppSettings.Load(profile.Paths)).LatestKnownVersion is null; i++) await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal("9.9.9", saved!.LatestKnownVersion);
        }
        finally
        {
            UpdateCheck.Endpoint = before;
        }
    }
}
