using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace VmQontrol.Core.Hosts;

/// <summary>The newest published release, as GitHub lists it.</summary>
public sealed record ReleaseInfo(string Version, string Url, DateTimeOffset? Published);

/// <summary>
/// "Is there a newer VM Qontrol?" — asked only when the person presses Check for updates (the app makes no connection
/// of its own). One request to GitHub's public "latest release" address, which skips pre-releases and drafts.
/// </summary>
public static class UpdateCheck
{
    public const string ReleasesPage = "https://github.com/codingncaffeine/VM-Qontrol/releases";

    /// <summary>Tests point this at a local server.</summary>
    internal static string Endpoint { get; set; } = "https://api.github.com/repos/codingncaffeine/VM-Qontrol/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The newest release; throws <see cref="IOException"/> with a sentence a person can read when it cannot be told.</summary>
    public static async Task<ReleaseInfo> LatestAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VM-Qontrol", ThisVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new IOException("GitHub lists no release yet.");
            if ((int)response.StatusCode is 403 or 429) throw new IOException("GitHub is limiting requests from this network for now; try again in an hour.");
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var tag = json?["tag_name"]?.GetValue<string>();
            if (Normalize(tag) is not { } version) throw new IOException("GitHub's answer named no version.");
            var url = json?["html_url"]?.GetValue<string>() is { } u && u.StartsWith("https://github.com/", StringComparison.Ordinal) ? u : ReleasesPage;
            var published = DateTimeOffset.TryParse(json?["published_at"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var p) ? p : (DateTimeOffset?)null;
            return new ReleaseInfo(version, url, published);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"GitHub could not be reached ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("GitHub did not answer within 15 seconds.");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new IOException("GitHub's answer could not be read.");
        }
    }

    /// <summary>This build's version without the source revision: "0.0.5".</summary>
    public static string ThisVersion => Normalize(BuildInfo.Version) ?? "0.0.0";

    /// <summary>"v0.0.6", "0.0.6+abc" → "0.0.6"; <c>null</c> when it is not a version.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimStart('v', 'V');
        var cut = t.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0) t = t[..cut];
        return Version.TryParse(t, out _) ? t : null;
    }

    /// <summary>Whether <paramref name="latest"/> is newer than <paramref name="current"/> (false when either is unreadable).</summary>
    public static bool IsNewer(string? latest, string? current) =>
        Normalize(latest) is { } l && Normalize(current) is { } c && Version.Parse(l) > Version.Parse(c);
}
