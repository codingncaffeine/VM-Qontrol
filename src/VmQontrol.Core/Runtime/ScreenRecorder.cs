using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace VmQontrol.Core.Runtime;

/// <summary>The video encoder a recording uses: H.264 in MP4 where ffmpeg has it, else VP9 or VP8 in WebM.</summary>
public sealed record RecordingFormat(string Encoder, string Extension, IReadOnlyList<string> Options)
{
    public static RecordingFormat? Choose(IReadOnlySet<string> encoders) =>
        encoders.Contains("libx264") ? new("libx264", ".mp4", ["-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart"])
        : encoders.Contains("libvpx-vp9") ? new("libvpx-vp9", ".webm", ["-deadline", "realtime", "-cpu-used", "8", "-b:v", "0", "-crf", "34", "-pix_fmt", "yuv420p"])
        : encoders.Contains("libvpx") ? new("libvpx", ".webm", ["-deadline", "realtime", "-cpu-used", "8", "-b:v", "4M", "-pix_fmt", "yuv420p"])
        : null;

    /// <summary>The encoders this computer's ffmpeg has, or <c>null</c> when ffmpeg is not installed.</summary>
    public static async Task<IReadOnlySet<string>?> EncodersAsync(string ffmpeg = "ffmpeg", CancellationToken ct = default)
    {
        try
        {
            var start = new ProcessStartInfo(ffmpeg) { ArgumentList = { "-hide_banner", "-encoders" }, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(start)!;
            var text = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return text.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(f => f.Length > 1 && f[0].Length == 6 && f[0][0] == 'V').Select(f => f[1]).ToHashSet(StringComparer.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Records a machine's screen to a video, as Workstation's Capture Movie does: frames of the guest's own display (QMP
/// screendump into the machine's runtime folder, which is in memory), handed to ffmpeg with the time each was taken,
/// so the video plays at the speed things happened whatever the frame rate managed. Works with any viewer, or none.
/// The size is fixed by the first frame; a guest that changes resolution is scaled into it.
/// </summary>
public sealed class ScreenRecorder : IAsyncDisposable
{
    private readonly Func<string, CancellationToken, Task> _screendump;
    private readonly string _frame;
    private readonly CancellationTokenSource _stop = new();
    private Process? _ffmpeg;
    private Task? _loop;

    private ScreenRecorder(Func<string, CancellationToken, Task> screendump, string runtimeDirectory, string output)
    {
        _screendump = screendump;
        _frame = Path.Combine(runtimeDirectory, "recording-frame.ppm");
        Output = output;
    }

    public string Output { get; }

    public int Frames { get; private set; }

    public DateTimeOffset Started { get; } = DateTimeOffset.Now;

    /// <summary>Why the recording ended by itself (the machine stopped, ffmpeg failed), or <c>null</c> while it runs.</summary>
    public string? Ended { get; private set; }

    public event Action? Stopped;

    /// <summary>A recorder that takes frames through the machine's QMP connection.</summary>
    public static Task<ScreenRecorder> StartAsync(MachineWatcher watcher, string output, RecordingFormat format, int framesPerSecond = 15, string ffmpeg = "ffmpeg", CancellationToken ct = default) =>
        StartAsync((file, token) => watcher.ExecuteAsync("screendump", new JsonObject { ["filename"] = file, ["format"] = "ppm" }, token), watcher.Machine.RuntimeDirectory, output, format, framesPerSecond, ffmpeg, ct);

    public static async Task<ScreenRecorder> StartAsync(Func<string, CancellationToken, Task> screendump, string runtimeDirectory, string output, RecordingFormat format,
        int framesPerSecond = 15, string ffmpeg = "ffmpeg", CancellationToken ct = default)
    {
        var recorder = new ScreenRecorder(screendump, runtimeDirectory, output);
        // The first frame says the size; a machine that cannot give one is not recorded.
        await screendump(recorder._frame, ct).ConfigureAwait(false);
        var first = await File.ReadAllBytesAsync(recorder._frame, ct).ConfigureAwait(false);
        var (w, h) = PpmSize(first) ?? throw new IOException("QEMU gave no picture of the screen.");
        w += w % 2;
        h += h % 2;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", "-f", "image2pipe", "-c:v", "ppm", "-use_wallclock_as_timestamps", "1", "-i", "-",
                     "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2", "-fps_mode", "vfr", "-c:v", format.Encoder, .. format.Options, "-an", output])
            start.ArgumentList.Add(a);
        recorder._ffmpeg = Process.Start(start) ?? throw new IOException("ffmpeg could not be started.");
        await recorder._ffmpeg.StandardInput.BaseStream.WriteAsync(first, ct).ConfigureAwait(false);
        recorder.Frames = 1;
        recorder._loop = Task.Run(() => recorder.LoopAsync(TimeSpan.FromSeconds(1.0 / framesPerSecond)), CancellationToken.None);
        Log.Info($"Recording the screen to {output} ({w}x{h}, {format.Encoder}).");
        return recorder;
    }

    private async Task LoopAsync(TimeSpan interval)
    {
        var clock = Stopwatch.StartNew();
        var next = interval;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _stop.Token).ConfigureAwait(false);
                // A slow frame moves the schedule on rather than queueing a burst to catch up.
                next = clock.Elapsed + interval;
                await _screendump(_frame, _stop.Token).ConfigureAwait(false);
                var bytes = await File.ReadAllBytesAsync(_frame, _stop.Token).ConfigureAwait(false);
                await _ffmpeg!.StandardInput.BaseStream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                Frames++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is QmpException or IOException or InvalidOperationException)
        {
            Ended = _ffmpeg is { HasExited: true } ? "ffmpeg stopped" : "the machine stopped";
            Log.Warn($"Recording to {Output} ended: {ex.Message}");
        }
    }

    /// <summary>Ends the recording and waits for ffmpeg to finish the file; returns ffmpeg's complaint, or <c>null</c>.</summary>
    public async Task<string?> StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is { } loop) await loop.ConfigureAwait(false);
        string? problem = null;
        if (_ffmpeg is { } p)
        {
            try
            {
                p.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            var errors = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            if (p.ExitCode != 0) problem = errors.Trim() is { Length: > 0 } e ? e : $"ffmpeg ended with code {p.ExitCode}";
            p.Dispose();
            _ffmpeg = null;
        }

        try
        {
            File.Delete(_frame);
        }
        catch (IOException)
        {
        }

        Log.Info($"Recording saved: {Output} ({Frames} frames).");
        Stopped?.Invoke();
        return problem;
    }

    /// <summary>Width and height from a binary PPM (P6) header.</summary>
    internal static (int W, int H)? PpmSize(ReadOnlySpan<byte> ppm)
    {
        if (ppm.Length < 11 || ppm[0] != 'P' || ppm[1] != '6') return null;
        var header = Encoding.ASCII.GetString(ppm[..Math.Min(64, ppm.Length)]);
        var fields = header.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 3 && int.TryParse(fields[1], CultureInfo.InvariantCulture, out var w) && int.TryParse(fields[2], CultureInfo.InvariantCulture, out var h) ? (w, h) : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ffmpeg is not null) await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
