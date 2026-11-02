using System.Text;

namespace VmQontrol.Core.Media;

/// <summary>
/// The identity fields of an ISO 9660 disc image, read from its primary volume descriptor (sector 16): what
/// osinfo-db's media rules match on. Reads 4 KB, never the whole image.
/// </summary>
public sealed record IsoVolume(string SystemId, string VolumeId, string PublisherId, string ApplicationId, long VolumeBytes, bool Bootable)
{
    private const int SectorSize = 2048;

    /// <summary>The descriptor, or <c>null</c> when the file is not an ISO 9660 image (an HTML error page saved as .iso, for one).</summary>
    public static IsoVolume? Read(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        return Read(stream);
    }

    public static IsoVolume? Read(Stream stream)
    {
        var sector = new byte[SectorSize];
        IsoVolume? primary = null;
        var bootable = false;
        // Volume descriptors run from sector 16 until the terminator (type 255).
        for (var n = 16; n < 32; n++)
        {
            stream.Position = (long)n * SectorSize;
            if (stream.ReadAtLeast(sector, SectorSize, throwOnEndOfStream: false) < SectorSize) break;
            if (!sector.AsSpan(1, 5).SequenceEqual("CD001"u8)) break;
            var type = sector[0];
            if (type == 0 && sector.AsSpan(7, 23).StartsWith("EL TORITO SPECIFICATION"u8)) bootable = true;
            if (type == 1 && primary is null)
            {
                var blocks = BitConverter.ToUInt32(sector, 80);
                var blockSize = BitConverter.ToUInt16(sector, 128);
                primary = new IsoVolume(Text(sector, 8, 32), Text(sector, 40, 32), Text(sector, 318, 128), Text(sector, 574, 128), (long)blocks * blockSize, false);
            }

            if (type == 255) break;
        }

        return primary is null ? null : primary with { Bootable = bootable };
    }

    private static string Text(byte[] sector, int offset, int length) => Encoding.ASCII.GetString(sector, offset, length).TrimEnd(' ', '\0');
}
