using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmQontrol.Core.Downloads;

/// <summary>One downloadable system release and edition, as quickget lists it.</summary>
public sealed record CatalogueEntry(string DisplayName, string Os, string Release, string Option, string IconUrl)
{
    public string Label => Option.Length > 0 ? $"{Release} · {Option}" : Release;
}

/// <summary>A system (Ubuntu, Fedora, Windows…) and its releases, newest first.</summary>
public sealed record CatalogueSystem(string Os, string DisplayName, string IconUrl, IReadOnlyList<CatalogueEntry> Entries);

/// <summary>
/// The download catalogue: quickget's list of systems, releases and editions (93 systems, 700+ entries), cached for a
/// day so the wizard opens at once and works offline. quickget is optional; without it the catalogue is empty and the
/// wizard offers installer discs only.
/// </summary>
public static class Catalogue
{
    public static string Program { get; set; } = "quickget";

    private sealed record Raw(
        [property: JsonPropertyName("Display Name")] string DisplayName,
        [property: JsonPropertyName("OS")] string Os,
        [property: JsonPropertyName("Release")] string Release,
        [property: JsonPropertyName("Option")] string Option,
        [property: JsonPropertyName("PNG")] string Png);

    public static async Task<IReadOnlyList<CatalogueSystem>> LoadAsync(QontrolPaths paths, bool refresh = false, CancellationToken ct = default)
    {
        var cache = Path.Combine(paths.Cache, "catalogue.json");
        string? json = null;
        if (!refresh && File.Exists(cache) && File.GetLastWriteTimeUtc(cache) > DateTime.UtcNow.AddDays(-1))
            json = await File.ReadAllTextAsync(cache, ct).ConfigureAwait(false);
        if (json is null)
        {
            json = await RunAsync(ct, "--list-json").ConfigureAwait(false);
            if (json.TrimStart().StartsWith('['))
            {
                Directory.CreateDirectory(paths.Cache);
                await File.WriteAllTextAsync(cache, json, ct).ConfigureAwait(false);
            }
            else if (File.Exists(cache))
            {
                json = await File.ReadAllTextAsync(cache, ct).ConfigureAwait(false); // offline: an old list beats none
            }
            else
            {
                return [];
            }
        }

        return Parse(json);
    }

    public static IReadOnlyList<CatalogueSystem> Parse(string json)
    {
        var raw = JsonSerializer.Deserialize<List<Raw>>(json) ?? [];
        return raw.GroupBy(r => r.Os)
            .Select(g => new CatalogueSystem(g.Key, g.First().DisplayName, g.First().Png,
                g.Select(r => new CatalogueEntry(r.DisplayName, r.Os, r.Release, r.Option, r.Png))
                 .OrderByDescending(e => e.Release, ReleaseComparer.Instance).ThenBy(e => e.Option, StringComparer.Ordinal).ToList()))
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static async Task<string> RunAsync(CancellationToken ct, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(Program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return await output.ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    /// <summary>Orders releases as people expect: 24.04 after 9.10, "rolling" and names after numbers.</summary>
    internal sealed class ReleaseComparer : IComparer<string>
    {
        public static readonly ReleaseComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = (x ?? "").Split('.', '-', '_');
            var b = (y ?? "").Split('.', '-', '_');
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var pa = i < a.Length ? a[i] : "";
                var pb = i < b.Length ? b[i] : "";
                var na = long.TryParse(pa, out var va);
                var nb = long.TryParse(pb, out var vb);
                var c = na && nb ? va.CompareTo(vb) : na ? 1 : nb ? -1 : string.CompareOrdinal(pa, pb);
                if (c != 0) return c;
            }

            return 0;
        }
    }
}
