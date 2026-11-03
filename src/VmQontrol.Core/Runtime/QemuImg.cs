using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace VmQontrol.Core.Runtime;

/// <summary>qemu-img, run as a child process. Each call is slow by UI standards: call from a worker.</summary>
public static class QemuImg
{
    public static string Program { get; set; } = "qemu-img";

    /// <summary>A new disk with quickemu's options (lazy refcounts; no copy-on-write on btrfs, where it slows disks badly).</summary>
    public static async Task CreateAsync(string file, string format, long sizeGiB, string preallocation = "off", CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var options = format == "qcow2" ? $"lazy_refcounts=on,preallocation={preallocation},nocow=on" : $"preallocation={preallocation}";
        await RunAsync(ct, "create", "-q", "-f", format, "-o", options, file, sizeGiB.ToString(CultureInfo.InvariantCulture) + "G").ConfigureAwait(false);
    }

    /// <summary>qemu-img info as JSON (virtual-size, actual-size, format, snapshots...). -U reads a disk a running machine holds.</summary>
    public static async Task<JsonObject> InfoAsync(string file, CancellationToken ct = default) =>
        JsonNode.Parse(await RunAsync(ct, "info", "-U", "--output=json", file).ConfigureAwait(false)) as JsonObject
        ?? throw new IOException($"qemu-img gave no information for {file}.");

    public static async Task<string> RunAsync(CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo(Program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("qemu-img could not be started.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new IOException("qemu-img is not installed. vmq doctor (or the welcome page) names the package that has it.");
        }

        using var owned = process;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException($"qemu-img {args[0]} failed: {(await error.ConfigureAwait(false)).Trim()}");
        return await output.ConfigureAwait(false);
    }
}
