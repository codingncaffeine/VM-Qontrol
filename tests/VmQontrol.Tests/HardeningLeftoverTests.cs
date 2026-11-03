using System.Diagnostics;
using VmQontrol.Core.Downloads;
using VmQontrol.Core.Import;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;
using VmQontrol.Core.Snapshots;

namespace VmQontrol.Tests;

/// <summary>
/// The phase 18 audit's five low findings, fixed in s11 (2026-11-03) before the first release. Each test carries a
/// control rooted in the real condition (a real full disk, a real process list), not only the rule.
/// </summary>
public sealed class HardeningLeftoverTests
{
    [Fact]
    public async Task A_full_disk_during_a_download_is_named_as_a_full_disk_not_a_dropped_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        // Control: /dev/full really answers every write with ENOSPC, the errno the helper reads.
        await using (var raw = new FileStream("/dev/full", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0))
        {
            var io = await Assert.ThrowsAsync<IOException>(async () => await raw.WriteAsync(new byte[4096], ct));
            Assert.Equal(28, io.HResult);
        }

        await using var output = new FileStream("/dev/full", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
        var failed = await Assert.ThrowsAsync<DownloadFailedException>(async () => await DownloadWrite.WriteAsync(output, new byte[4096], ct));
        Assert.Contains("disk is full", failed.Message);
        Assert.DoesNotContain("connection", failed.Message);
        // Not an IOException any more: the downloads' "connection dropped; resuming" catch cannot take it.
        Assert.IsNotAssignableFrom<IOException>(failed);
    }

    [Fact]
    public void A_download_folder_that_cannot_be_written_is_named()
    {
        using var profile = new TestProfile("leftover-perm");
        var dir = Path.Combine(profile.Root, "locked");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // Probe: root (the Alpine CI container) writes through any folder mode, so there is no fault to see there.
            var probe = Path.Combine(dir, "probe");
            var writable = true;
            try { File.WriteAllText(probe, ""); File.Delete(probe); }
            catch (UnauthorizedAccessException) { writable = false; }
            Assert.SkipWhen(writable, "this user writes through folder permissions (root)");
            var failed = Assert.Throws<DownloadFailedException>(() => DownloadWrite.Open(Path.Combine(dir, "x.part"), append: false));
            Assert.Contains("no permission", failed.Message);
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Control: the same folder, writable again, opens.
        using var ok = DownloadWrite.Open(Path.Combine(dir, "x.part"), append: false);
        Assert.True(File.Exists(Path.Combine(dir, "x.part")));
    }

    [Fact]
    public void A_quickemu_array_left_open_is_read_in_one_pass()
    {
        // 200,000 lines after an unclosed "(": rescanning the growing body per line was ~10^11 character steps.
        var text = "guest_os=\"linux\"\nfixed_iso=(\n" + string.Concat(Enumerable.Repeat("\"word\"\n", 200_000));
        var clock = Stopwatch.StartNew();
        var conf = QuickemuConf.Parse(text);
        clock.Stop();
        Assert.Equal(200_000, conf.Array("fixed_iso").Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");

        // Control: a closed array over several lines, with a ")" inside quotes on another line, reads as before.
        var closed = QuickemuConf.Parse("port_forwards=(\"22:22\" \"a)\n b\"\n\"8080:80\")\nram=\"4G\"\n");
        Assert.Equal(["22:22", "a) b", "8080:80"], closed.Array("port_forwards"));
        Assert.Equal("4G", closed["ram"]);
    }

    [Fact]
    public void The_easy_install_password_comes_from_a_file_or_standard_input_not_the_process_list()
    {
        using var profile = new TestProfile("leftover-pw");
        var file = Path.Combine(profile.Root, "pw");
        File.WriteAllText(file, "s3cret pass\n");
        string? Opt(string[] a, string flag) => Array.IndexOf(a, flag) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;

        string[] fromFile = ["--password-file", file];
        Assert.True(VmQontrol.Cli.Commands.ReadPassword(f => Opt(fromFile, f), TextReader.Null, TextWriter.Null, out var p1));
        Assert.Equal("s3cret pass", p1);
        string[] fromStdin = ["--password-file", "-"];
        Assert.True(VmQontrol.Cli.Commands.ReadPassword(f => Opt(fromStdin, f), new StringReader("piped\r\n"), TextWriter.Null, out var p2));
        Assert.Equal("piped", p2);
        var err = new StringWriter();
        string[] both = ["--password", "x", "--password-file", file];
        Assert.False(VmQontrol.Cli.Commands.ReadPassword(f => Opt(both, f), TextReader.Null, err, out _));
        Assert.Contains("not both", err.ToString());
        // Control: --password alone still works for existing scripts.
        string[] plain = ["--password", "Qontrol-1"];
        Assert.True(VmQontrol.Cli.Commands.ReadPassword(f => Opt(plain, f), TextReader.Null, TextWriter.Null, out var p3));
        Assert.Equal("Qontrol-1", p3);
    }

    [Fact]
    public async Task Restoring_an_offline_snapshot_on_several_disks_is_all_or_none()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("leftover-restore");
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "two");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig { Name = "two", Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }, new DiskConfig { File = "disk1.qcow2", SizeGiB = 1 }] };
        var d0 = Path.Combine(dir, "disk0.qcow2");
        var d1 = Path.Combine(dir, "disk1.qcow2");
        await QemuImg.CreateAsync(d0, "qcow2", 1, ct: ct);
        await QemuImg.CreateAsync(d1, "qcow2", 1, ct: ct);
        var store = new SnapshotStore(m, dir, Path.Combine(profile.Root, "meta"));
        var snap = await store.TakeAsync("Empty", "", null, ct);
        await QemuIo(d0, "write -P 0xaa 0 64k", ct);
        await QemuIo(d1, "write -P 0xaa 0 64k", ct);

        // The second disk lost the snapshot: the first must not be left returned while the second is not.
        await QemuImg.RunAsync(ct, "snapshot", "-d", snap.Tag, d1);
        await Assert.ThrowsAsync<IOException>(() => store.RestoreAsync(snap, null, ct));
        await QemuIo(d0, "read -P 0xaa 0 64k", ct); // the new data is still there (throws if it was returned to empty)
        await Assert.ThrowsAsync<IOException>(() => QemuIo(d0, "read -P 0 0 64k", ct));
        Assert.DoesNotContain("vmq-undo", await QemuImg.RunAsync(ct, "snapshot", "-l", d0));
        Assert.DoesNotContain("vmq-undo", await QemuImg.RunAsync(ct, "snapshot", "-l", d1));

        // Control: with the snapshot on both disks, the restore returns both (the pattern is gone from disk0).
        var both = await store.TakeAsync("Again", "", null, ct);
        await QemuIo(d0, "write -P 0xbb 0 64k", ct);
        await QemuIo(d1, "write -P 0xbb 0 64k", ct);
        await store.RestoreAsync(both, null, ct);
        await QemuIo(d0, "read -P 0xaa 0 64k", ct);
        await QemuIo(d1, "read -P 0xaa 0 64k", ct);
        await Assert.ThrowsAsync<IOException>(() => QemuIo(d0, "read -P 0xbb 0 64k", ct));
    }

    /// <summary>Runs one qemu-io command on a disk; a failed pattern check throws (its output names the mismatch).</summary>
    private static async Task<string> QemuIo(string disk, string command, CancellationToken ct)
    {
        var start = new ProcessStartInfo("qemu-io") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-f", "qcow2", "-c", command, disk]) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (output.Contains("Pattern verification failed", StringComparison.Ordinal) || p.ExitCode != 0) throw new IOException($"qemu-io {command}: {output}");
        return output;
    }
}
