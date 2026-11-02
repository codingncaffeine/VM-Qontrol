using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;

namespace VmQontrol.Core.Media;

/// <summary>An edition inside a Windows installation image (Home, Pro, Education…), by its index in the image.</summary>
public sealed record WindowsEdition(int Index, string Name, string DisplayName, int Build = 0);

/// <summary>What a Windows installation disc really holds.</summary>
public sealed record WindowsMediaInfo(int Build, string Branch, string Architecture, string Release, bool Server, IReadOnlyList<WindowsEdition> Editions)
{
    /// <summary>"Windows 11 (build 26100)" — for the wizard.</summary>
    public string Description => $"Windows {(Server ? "Server " : "")}{Release} (build {Build})";
}

/// <summary>
/// Names the exact Windows release on a disc. Windows 10 and 11 discs share one volume label, so osinfo-db (and
/// libosinfo's own osinfo-detect) cannot tell them apart; the build number in sources/idwbinfo.txt can. The editions
/// come from the install image's XML catalogue, which a WIM or ESD file keeps uncompressed after its header.
/// </summary>
public static class WindowsMedia
{
    public static WindowsMediaInfo? Inspect(string isoFile)
    {
        using var udf = UdfReader.Open(isoFile);
        return udf is null ? null : Inspect(udf);
    }

    public static WindowsMediaInfo? Inspect(UdfReader udf)
    {
        if (udf.ReadFile("sources/idwbinfo.txt", 64 * 1024) is not { } info) return null;
        var fields = Encoding.ASCII.GetString(info).Split('\n').Select(l => l.Trim().Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var editions = Editions(udf, "sources/install.wim") ?? Editions(udf, "sources/install.esd") ?? [];
        // The image catalogue names the exact build; the branch is the fallback (Windows 10 22H2's file has no build at all).
        var build = editions.Select(e => e.Build).FirstOrDefault(b => b > 0);
        if (build == 0 && int.TryParse(fields.GetValueOrDefault("BuildBuild"), NumberStyles.None, CultureInfo.InvariantCulture, out var stated)) build = stated;
        if (build == 0) build = BranchBuild(fields.GetValueOrDefault("BuildBranch") ?? "");
        if (build == 0) return null;
        var server = editions.Any(e => e.Name.Contains("Server", StringComparison.OrdinalIgnoreCase));
        var release = server ? ServerRelease(build) : build >= 22000 ? "11" : build >= 10240 ? "10" : build >= 9600 ? "8.1" : build >= 9200 ? "8" : "7";
        return new WindowsMediaInfo(build, fields.GetValueOrDefault("BuildBranch") ?? "", fields.GetValueOrDefault("BuildArch") ?? "", release, server, editions);
    }

    /// <summary>The first build of each Windows development branch, for discs whose catalogue cannot be read.</summary>
    public static int BranchBuild(string branch) => branch.Split('_')[0].ToLowerInvariant() switch
    {
        "th1" => 10240,
        "th2" => 10586,
        "rs1" => 14393,
        "rs2" => 15063,
        "rs3" => 16299,
        "rs4" => 17134,
        "rs5" => 17763,
        "19h1" => 18362,
        "vb" => 19041,
        "co" => 22000,
        "ni" => 22621,
        "ge" => 26100,
        _ => 0,
    };

    private static string ServerRelease(int build) => build switch
    {
        >= 26100 => "2025",
        >= 20348 => "2022",
        >= 17763 => "2019",
        >= 14393 => "2016",
        _ => "",
    };

    /// <summary>The editions in a WIM/ESD image, from the XML resource its header points at.</summary>
    public static IReadOnlyList<WindowsEdition>? Editions(UdfReader udf, string path)
    {
        if (udf.ReadAt(path, 0, 208) is not { } header || !header.AsSpan(0, 5).SequenceEqual("MSWIM"u8)) return null;
        // The XML data resource header: 7-byte size + flags, then the offset.
        var size = (long)(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72)) & 0x00FF_FFFF_FFFF_FFFF);
        var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(80));
        if (size <= 0 || size > 8 * 1024 * 1024 || udf.ReadAt(path, offset, (int)size) is not { } xmlBytes) return null;
        return ParseCatalogue(Encoding.Unicode.GetString(xmlBytes).TrimStart('﻿'));
    }

    public static IReadOnlyList<WindowsEdition> ParseCatalogue(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml(xml);
        var editions = new List<WindowsEdition>();
        foreach (XmlElement image in doc.SelectNodes("/WIM/IMAGE")!)
        {
            if (!int.TryParse(image.GetAttribute("INDEX"), NumberStyles.None, CultureInfo.InvariantCulture, out var index)) continue;
            var name = image.SelectSingleNode("NAME")?.InnerText.Trim() ?? "";
            var display = image.SelectSingleNode("DISPLAYNAME")?.InnerText.Trim() is { Length: > 0 } d ? d : name;
            int.TryParse(image.SelectSingleNode("WINDOWS/VERSION/BUILD")?.InnerText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var build);
            editions.Add(new WindowsEdition(index, name, display, build));
        }

        return editions;
    }
}
