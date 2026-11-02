using VmQontrol.Core;

namespace VmQontrol.Tests;

/// <summary>
/// A throwaway profile under the repository's scratch folder (never /tmp, never the real profile): its own config, state
/// and library. Deleted when the test ends.
/// </summary>
internal sealed class TestProfile : IDisposable
{
    public TestProfile(string? name = null)
    {
        Root = Path.Combine(ScratchRoot, "test-profiles", $"{name ?? "p"}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Paths = QontrolPaths.FromEnvironment(v => v == QontrolPaths.HomeVariable ? Root : null);
        Paths.EnsureCreated();
    }

    public string Root { get; }

    public QontrolPaths Paths { get; }

    /// <summary>The repository's scratch folder, found from the test binary's location.</summary>
    public static string ScratchRoot { get; } = FindScratch();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FindScratch()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VmQontrol.slnx"))) return Path.Combine(dir.FullName, "scratch");
        }

        throw new InvalidOperationException("The repository root (VmQontrol.slnx) was not found above the test binary.");
    }
}
