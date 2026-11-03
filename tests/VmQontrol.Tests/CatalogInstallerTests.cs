using System.Text.Json.Nodes;
using VmQontrol.Core.Downloads;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// Phase 4's acceptance, by the wizard's own path: a catalogue entry downloads through quickget (for macOS, Apple's
/// recovery image + OpenCore), becomes a machine, and boots towards its installer. Screens are saved to
/// snapshots/catalogue/OS-RELEASE to be looked at. It downloads gigabytes, so it runs only when asked:
/// VMQONTROL_CATALOGUE_PROOF="macos sonoma" or "ubuntu 24.04" (OS RELEASE [EDITION], as quickget takes them).
/// </summary>
[Trait("Tier", "Guest")]
public sealed class CatalogueInstallerTests
{
    [Fact]
    public async Task A_catalogue_system_reaches_its_installer()
    {
        var words = (Environment.GetEnvironmentVariable("VMQONTROL_CATALOGUE_PROOF") ?? "").Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        Assert.SkipWhen(words.Length < 2, "Set VMQONTROL_CATALOGUE_PROOF=\"OS RELEASE [EDITION]\" to download that system and boot it.");
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("catalogue-proof");
        var entry = new CatalogueEntry(words[0], words[0], words[1], words.Length > 2 ? words[2] : "", "");
        var (m, dir) = await new DownloadJob(entry, profile.Paths.DefaultLibrary, $"{words[0]} {words[1]}").RunAsync(new Progress<DownloadProgress>(_ => { }), ct);

        var shots = Path.Combine(TestProfile.ScratchRoot, "..", "snapshots", "catalogue", string.Join('-', words));
        Directory.CreateDirectory(shots);
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            await using var watcher = await MachineWatcher.AttachAsync(running, ct);
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                Assert.True(MachineRunner.Find(m, profile.Paths) is not null, $"the machine stopped after {(i + 1) * 30} s");
                var file = Path.GetFullPath(Path.Combine(shots, $"{(i + 1) * 30:D3}s.png"));
                await watcher.ExecuteAsync("screendump", new JsonObject { ["filename"] = file, ["format"] = "png" }, ct);
                TestContext.Current.TestOutputHelper?.WriteLine($"screen at {(i + 1) * 30} s: {file}");
            }
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }
    }
}
