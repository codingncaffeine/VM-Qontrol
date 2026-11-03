namespace VmQontrol.Core.Downloads;

/// <summary>
/// Writing a download to this computer's disk. A failure here belongs to the disk (full, over quota, read-only, gone),
/// never to the connection: it is named as such and not retried as a dropped connection would be.
/// </summary>
internal static class DownloadWrite
{
    private const int NoSpace = 28, ReadOnlyFileSystem = 30, OverQuota = 122; // ENOSPC, EROFS, EDQUOT

    /// <summary>
    /// Opens the partial file. Unbuffered on purpose: every write reaches the disk inside <see cref="WriteAsync"/>, so
    /// closing the file never flushes (and never fails) where it would be mistaken for the connection.
    /// </summary>
    public static FileStream Open(string partial, bool append)
    {
        try
        {
            return new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Failed(ex, partial);
        }
    }

    public static async ValueTask WriteAsync(FileStream output, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            await output.WriteAsync(data, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw Failed(ex, output.Name);
        }
    }

    internal static DownloadFailedException Failed(Exception ex, string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
        return ex.HResult switch
        {
            NoSpace or OverQuota => new DownloadFailedException([$"The disk is full: {folder} has no room left for the download{Free(folder)}. Free some space and try again; what arrived is kept and the download carries on from there."]),
            ReadOnlyFileSystem => new DownloadFailedException([$"{folder} is read-only, so the download cannot be saved there."]),
            _ when ex is UnauthorizedAccessException => new DownloadFailedException([$"{folder} cannot be written to (no permission), so the download cannot be saved there."]),
            _ => new DownloadFailedException([$"The download could not be written to {folder} ({ex.Message})."]),
        };
    }

    private static string Free(string folder)
    {
        try
        {
            return $" ({new DriveInfo(folder).AvailableFreeSpace / (1024 * 1024)} MB free)";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
