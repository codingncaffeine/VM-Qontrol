using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using VmQontrol.Core.Media;

namespace VmQontrol.Core.OsInfo;

/// <summary>One operating system in osinfo-db: its name, how to recognise its media, and what it needs.</summary>
public sealed record OsEntry(
    string Id,
    string ShortId,
    string Name,
    string Family,
    string Distro,
    string Version,
    string? ReleaseDate,
    IReadOnlyList<OsMedia> Media,
    IReadOnlyDictionary<string, OsResources> Minimum,
    IReadOnlyDictionary<string, OsResources> Recommended,
    IReadOnlyList<string> InstallScripts);

/// <summary>A media rule: every field present must match the disc's field (regular expressions, as libosinfo applies them).</summary>
public sealed record OsMedia(string Arch, string? VolumeId, string? SystemId, string? PublisherId, string? ApplicationId, long? VolumeSize, bool Installer);

public sealed record OsResources(int? Cpus, long? RamBytes, long? StorageBytes);

/// <summary>A media match, ranked: <see cref="Ambiguous"/> lists the other systems that match the disc just as well.</summary>
public sealed record OsMatch(OsEntry Os, OsMedia Media, IReadOnlyList<OsEntry> Ambiguous);

/// <summary>
/// osinfo-db, the operating-system database virt-manager, GNOME Boxes and virt-install use, read as XML data (no
/// libosinfo binding). Loading parses about a thousand files: do it once, off the UI thread.
/// </summary>
public sealed class OsInfoDb
{
    private OsInfoDb(List<OsEntry> entries) => Entries = entries;

    public IReadOnlyList<OsEntry> Entries { get; }

    /// <summary>The database folders libosinfo reads, system first: OSINFO_SYSTEM_DIR, then the usual install places.</summary>
    public static IEnumerable<string> SearchPath()
    {
        if (Environment.GetEnvironmentVariable("OSINFO_SYSTEM_DIR") is { Length: > 0 } custom) yield return custom;
        yield return "/usr/share/osinfo";
        yield return "/usr/local/share/osinfo";
    }

    public static OsInfoDb? LoadInstalled() => SearchPath().FirstOrDefault(d => Directory.Exists(Path.Combine(d, "os"))) is { } dir ? Load(dir) : null;

    public static OsInfoDb Load(string root)
    {
        var entries = new List<OsEntry>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "os"), "*.xml", SearchOption.AllDirectories))
        {
            // The NAME.d folders hold driver overlays for an entry already read; detection needs none of them.
            if (Path.GetFileName(Path.GetDirectoryName(file))!.EndsWith(".d", StringComparison.Ordinal)) continue;
            try
            {
                entries.AddRange(Parse(file));
            }
            catch (XmlException ex)
            {
                Log.Warn($"osinfo-db: {file} could not be read ({ex.Message}); skipped.");
            }
        }

        return new OsInfoDb(entries);
    }

    public OsEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id || e.ShortId == id);

    /// <summary>The operating system on a disc, or <c>null</c> when nothing in the database recognises it.</summary>
    public OsMatch? Identify(IsoVolume iso, string arch = "x86_64")
    {
        var hits = new List<(OsEntry Os, OsMedia Media, int Score)>();
        foreach (var os in Entries)
        {
            foreach (var media in os.Media)
            {
                if (!Matches(media, iso)) continue;
                // Prefer this machine's architecture, then the rule that pins down more fields.
                var score = (media.Arch == arch ? 100 : media.Arch is "all" or "" ? 50 : 0)
                            + new[] { media.VolumeId, media.SystemId, media.PublisherId, media.ApplicationId }.Count(f => f is not null) * 10
                            + (media.VolumeSize is not null ? 5 : 0);
                hits.Add((os, media, score));
            }
        }

        if (hits.Count == 0) return null;
        var best = hits.MaxBy(h => h.Score);
        var tied = hits.Where(h => h.Score == best.Score && h.Os.Id != best.Os.Id).Select(h => h.Os).DistinctBy(o => o.Id).ToList();
        // Among systems the disc fits equally, the newest is first (Windows 10 and 11 share a volume label).
        var ordered = tied.Append(best.Os).OrderByDescending(o => o.ReleaseDate ?? "", StringComparer.Ordinal).ToList();
        return new OsMatch(ordered[0], best.Media, ordered.Skip(1).ToList());
    }

    private static bool Matches(OsMedia media, IsoVolume iso)
    {
        if (media.VolumeId is null && media.SystemId is null && media.PublisherId is null && media.ApplicationId is null) return false;
        return Field(media.VolumeId, iso.VolumeId) && Field(media.SystemId, iso.SystemId) && Field(media.PublisherId, iso.PublisherId)
               && Field(media.ApplicationId, iso.ApplicationId) && (media.VolumeSize is null || media.VolumeSize == iso.VolumeBytes);
    }

    private static bool Field(string? pattern, string value)
    {
        if (pattern is null) return true;
        if (value.Length == 0) return false;
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            return string.Equals(pattern, value, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<OsEntry> Parse(string file)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            doc.Load(reader);
        foreach (XmlElement os in doc.SelectNodes("/libosinfo/os")!)
        {
            string? Child(string name) => os.SelectSingleNode($"{name}[not(@xml:lang)]", Ns(doc))?.InnerText.Trim();
            var media = new List<OsMedia>();
            foreach (XmlElement m in os.SelectNodes("media")!)
            {
                if (m.SelectSingleNode("iso") is not XmlElement iso) continue;
                string? Iso(string name) => iso.SelectSingleNode(name)?.InnerText.Trim() is { Length: > 0 } v ? v : null;
                media.Add(new OsMedia(m.GetAttribute("arch"), Iso("volume-id"), Iso("system-id"), Iso("publisher-id"), Iso("application-id"),
                    long.TryParse(Iso("volume-size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : null,
                    m.GetAttribute("installer") != "false"));
            }

            var minimum = new Dictionary<string, OsResources>(StringComparer.Ordinal);
            var recommended = new Dictionary<string, OsResources>(StringComparer.Ordinal);
            foreach (XmlElement r in os.SelectNodes("resources")!)
            {
                var arch = r.GetAttribute("arch");
                if (Resources(r, "minimum") is { } min) minimum[arch] = min;
                if (Resources(r, "recommended") is { } rec) recommended[arch] = rec;
            }

            var scripts = os.SelectNodes("installer/script")!.Cast<XmlElement>().Select(s => s.GetAttribute("id")).ToList();
            yield return new OsEntry(os.GetAttribute("id"), Child("short-id") ?? "", Child("name") ?? os.GetAttribute("id"),
                Child("family") ?? "", Child("distro") ?? "", Child("version") ?? "", Child("release-date"), media, minimum, recommended, scripts);
        }
    }

    private static OsResources? Resources(XmlElement r, string kind)
    {
        if (r.SelectSingleNode(kind) is not XmlElement e) return null;
        long? Long(string n) => long.TryParse(e.SelectSingleNode(n)?.InnerText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        var cpus = Long("n-cpus");
        return new OsResources(cpus is null ? null : (int)cpus, Long("ram"), Long("storage"));
    }

    private static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("xml", "http://www.w3.org/XML/1998/namespace");
        return ns;
    }
}
