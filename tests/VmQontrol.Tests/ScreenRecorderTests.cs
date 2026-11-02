using System.Diagnostics;
using System.Text.Json.Nodes;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using Avalonia.VisualTree;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

public sealed class ScreenRecorderTests
{
    [Fact]
    public void The_encoder_is_H264_where_there_is_one_and_WebM_otherwise()
    {
        Assert.Equal(".mp4", RecordingFormat.Choose(new HashSet<string> { "libx264", "libvpx-vp9" })!.Extension);
        Assert.Equal("libvpx-vp9", RecordingFormat.Choose(new HashSet<string> { "libvpx-vp9", "libvpx" })!.Encoder);
        Assert.Equal(".webm", RecordingFormat.Choose(new HashSet<string> { "libvpx" })!.Extension);
        Assert.Null(RecordingFormat.Choose(new HashSet<string> { "mpeg4" }));
    }

    [Fact]
    public void A_PPM_header_gives_the_screen_size() =>
        Assert.Equal((1280, 800), ScreenRecorder.PpmSize("P6\n1280 800\n255\n\0\0\0"u8));

    /// <summary>
    /// A real machine's screen recorded for about four seconds: the file is a video of the guest's display size, it
    /// lasts as long as the recording did (frames carry the time they were taken), and the machine keeps running.
    /// </summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_running_machines_screen_is_recorded_at_its_real_speed()
    {
        var ct = TestContext.Current.CancellationToken;
        var encoders = await RecordingFormat.EncodersAsync(ct: ct);
        Assert.SkipWhen(encoders is null || !File.Exists("/usr/bin/ffprobe"), "ffmpeg is not installed.");
        var format = RecordingFormat.Choose(encoders!);
        Assert.SkipWhen(format is null, "ffmpeg has none of the encoders a recording uses.");
        using var profile = new TestProfile("record");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "rec");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "rec", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        var output = Path.Combine(profile.Root, "videos", "rec" + format!.Extension);
        try
        {
            await using var watcher = await MachineWatcher.AttachAsync(running, ct);
            var clock = Stopwatch.StartNew();
            var recorder = await ScreenRecorder.StartAsync(watcher, output, format, ct: ct);
            await Task.Delay(4000, ct);
            Assert.Null(await recorder.StopAsync());
            var seconds = clock.Elapsed.TotalSeconds;
            Assert.True(recorder.Frames >= 20, $"only {recorder.Frames} frames in {seconds:0.0} s");
            Assert.Equal("running", await MachineRunner.StatusAsync(running, ct));

            var probe = await ProbeAsync(output, ct);
            var stream = probe["streams"]![0]!;
            Assert.Equal(format.Encoder == "libx264" ? "h264" : format.Encoder == "libvpx-vp9" ? "vp9" : "vp8", stream["codec_name"]!.GetValue<string>());
            Assert.True(stream["width"]!.GetValue<int>() >= 320);
            var duration = double.Parse(probe["format"]!["duration"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, seconds - 1.5, seconds + 0.5);
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    private static async Task<JsonNode> ProbeAsync(string file, CancellationToken ct)
    {
        var start = new ProcessStartInfo("/usr/bin/ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-v", "error", "-print_format", "json", "-show_format", "-show_streams", file]) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var json = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return JsonNode.Parse(json)!;
    }
}

/// <summary>
/// Recording from the window, the way a person does it: ⋯, Record the screen; the red mark with the time shows beside
/// the machine's state; its stop button saves the video (inside the test's own profile, never the real Videos folder).
/// </summary>
public sealed class RecordingWindowTests
{
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task Recording_starts_from_the_menu_and_stops_from_the_red_mark()
    {
        var ct = TestContext.Current.CancellationToken;
        var encoders = await RecordingFormat.EncodersAsync(ct: ct);
        Assert.SkipWhen(encoders is null || RecordingFormat.Choose(encoders) is null, "ffmpeg with H.264 or VP9 is not installed.");
        using var profile = new TestProfile("record-ui");
        Assert.True(profile.Paths.Isolated);
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Rec");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "Rec", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios }, Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }] };
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        MachineStore.Save(m, Path.Combine(dir, "vm.json"));
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            var seen = await Ui.Run(() =>
            {
                var shell = new VmQontrol.App.ViewModels.ShellViewModel(profile.Paths, VmQontrol.Core.AppSettings.Load(profile.Paths)) { WatchUsb = false };
                var window = new VmQontrol.App.Views.MainWindow { DataContext = shell, Width = 1320, Height = 860 };
                Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                window.Show();
                _ = shell.StartAsync();
                Ui.PumpUntil(() => shell.Machines.Count == 1, "the library");
                Ui.Click(window, Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.ListBox>().First(l => l.Name == "Library")
                    .GetVisualDescendants().OfType<Avalonia.Controls.ListBoxItem>().First());
                Ui.PumpUntil(() => shell.Selected is { IsRunning: true }, "the running machine", 20);
                Ui.Pump(0.3);
                var page = window.GetVisualDescendants().OfType<VmQontrol.App.Views.MachineView>().Single();
                Ui.Click(window, page.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First(b => b.Name == "More"));
                Avalonia.Controls.MenuItem? item = null;
                Ui.PumpUntil(() => (item = window.GetVisualDescendants().OfType<Avalonia.Controls.MenuFlyoutPresenter>().FirstOrDefault()?.GetRealizedContainers()
                    .OfType<Avalonia.Controls.MenuItem>().FirstOrDefault(i => i.Name == "RecordScreenItem")) is not null, "the ⋯ menu");
                Ui.Click(window, item!);
                Ui.PumpUntil(() => shell.Selected!.IsRecording, "the recording to start", 15);
                Ui.Pump(2.5);
                var pill = page.GetVisualDescendants().OfType<Avalonia.Controls.Border>().First(b => b.Name == "RecordingPill");
                var pillShown = pill.IsEffectivelyVisible;
                var text = shell.Selected!.RecordingText;
                Ui.Capture(window, "phase9", "recording");
                Ui.Click(window, page.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First(b => b.Name == "StopRecording"));
                Ui.PumpUntil(() => !shell.Selected!.IsRecording, "the recording to be saved", 15);
                window.Close();
                return (pillShown, text);
            });

            Assert.True(seen.pillShown, "the red mark shows while recording");
            Assert.StartsWith("Recording 0:0", seen.text, StringComparison.Ordinal);
            var video = Assert.Single(Directory.GetFiles(Path.Combine(profile.Paths.Data, "Videos")));
            Assert.StartsWith("Rec ", Path.GetFileName(video), StringComparison.Ordinal);
            Assert.True(new FileInfo(video).Length > 1000, "the video has frames in it");
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }
}
