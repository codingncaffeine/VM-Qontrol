using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// Real QEMU, real KVM: tiny headless machines in a throwaway profile (no screen, no sound, 256 MB), each powered off in
/// a finally block. They boot no operating system; the firmware's own screen is enough to prove control works.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class RunnerIntegrationTests
{
    private static readonly HostCapabilities Host = HostProbe.Probe();

    private static async Task<(TestProfile Profile, MachineConfig Machine, string Dir)> NewMachineAsync(string name, Func<MachineConfig, MachineConfig>? edit = null)
    {
        var profile = new TestProfile(name);
        var dir = Path.Combine(profile.Paths.DefaultLibrary, name);
        Directory.CreateDirectory(dir);
        var machine = new MachineConfig
        {
            Name = name,
            MemoryMiB = 256,
            Cpu = new CpuConfig { Cores = 1 },
            Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
        };
        machine = edit?.Invoke(machine) ?? machine;
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: TestContext.Current.CancellationToken);
        MachineStore.Save(machine, Path.Combine(dir, MachineStore.FileName));
        return (profile, machine, dir);
    }

    [Fact]
    public async Task A_machine_starts_reports_pauses_resumes_takes_a_screenshot_and_powers_off()
    {
        var ct = TestContext.Current.CancellationToken;
        var (profile, m, dir) = await NewMachineAsync("runner");
        using var _ = profile;
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, Host, new StartOptions { Headless = true }, ct);
        try
        {
            // The control: the process really is QEMU with no sound device backend but "none".
            var cmdline = await File.ReadAllTextAsync($"/proc/{running.Pid}/cmdline", ct);
            Assert.Contains("none,id=audio0", cmdline, StringComparison.Ordinal);
            Assert.DoesNotContain("spice,id=audio0", cmdline, StringComparison.Ordinal);

            Assert.Equal("running", await MachineRunner.StatusAsync(running, ct));
            Assert.NotNull(MachineRunner.Find(m, profile.Paths));
            await Assert.ThrowsAsync<StartRefusedException>(() => MachineRunner.StartAsync(m, dir, profile.Paths, Host, new StartOptions { Headless = true }, ct));

            await MachineRunner.PauseAsync(running, ct);
            Assert.Equal("paused", await MachineRunner.StatusAsync(running, ct));
            await MachineRunner.ResumeAsync(running, ct);
            Assert.Equal("running", await MachineRunner.StatusAsync(running, ct));
            await MachineRunner.ResetAsync(running, ct);

            var png = Path.Combine(profile.Root, "screen.png");
            await MachineRunner.ScreenshotAsync(running, png, ct);
            var header = (await File.ReadAllBytesAsync(png, ct))[..8];
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }

        Assert.True(await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), ct));
        Assert.Null(MachineRunner.Find(m, profile.Paths));
        Assert.False(Directory.Exists(running.RuntimeDirectory));
    }

    [Fact]
    public async Task A_uefi_machine_with_a_TPM_gets_its_own_firmware_variables_and_a_TPM_that_goes_with_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (profile, m, dir) = await NewMachineAsync("tpm", x => x with { Firmware = new FirmwareConfig(), Tpm = true });
        using var _ = profile;
        Assert.SkipWhen(FirmwareLocator.Find(Host.FirmwareFiles, false) is null, "no OVMF firmware on this host");
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, Host, new StartOptions { Headless = true }, ct);
        int? swtpm;
        try
        {
            Assert.True(File.Exists(Path.Combine(dir, "OVMF_VARS.fd")));
            Assert.True(Directory.Exists(Path.Combine(dir, "tpm")));
            swtpm = int.Parse((await File.ReadAllTextAsync(Path.Combine(running.RuntimeDirectory, "swtpm.pid"), ct)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(Directory.Exists($"/proc/{swtpm}"));
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }

        Assert.True(await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), ct));
        // swtpm runs with --terminate: it ends with the machine.
        for (var i = 0; i < 50 && Directory.Exists($"/proc/{swtpm}"); i++) await Task.Delay(100, ct);
        Assert.False(Directory.Exists($"/proc/{swtpm}"));
    }

    [Fact]
    public async Task QEMUs_own_reason_comes_back_when_it_refuses_to_start()
    {
        var ct = TestContext.Current.CancellationToken;
        var (profile, m, dir) = await NewMachineAsync("refused", x => x with { ExtraArgs = ["-device", "no-such-device-xyz"] });
        using var _ = profile;
        var ex = await Assert.ThrowsAsync<StartRefusedException>(() => MachineRunner.StartAsync(m, dir, profile.Paths, Host, new StartOptions { Headless = true }, ct));
        Assert.Contains("no-such-device-xyz", ex.Message, StringComparison.Ordinal);
        Assert.Null(MachineRunner.Find(m, profile.Paths));
        Assert.False(Directory.Exists(profile.Paths.RuntimeFor(m.Id)), "a refused start left its runtime folder");
    }

    [Fact]
    public async Task A_machine_with_nothing_to_boot_is_refused_with_the_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("noboot");
        var m = new MachineConfig
        {
            Name = "noboot",
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
            Media = [new MediaConfig { Role = MediaRole.Installer, File = "/nonexistent/win.iso", Connect = Connect.UntilInstalled }],
        };
        var ex = await Assert.ThrowsAsync<StartRefusedException>(() => MachineRunner.StartAsync(m, profile.Root, profile.Paths, Host, new StartOptions { Headless = true }, ct));
        Assert.Contains(ex.Reasons, r => r.Contains("/nonexistent/win.iso", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_watcher_follows_pause_resume_and_the_end_of_the_machine()
    {
        var ct = TestContext.Current.CancellationToken;
        var (profile, m, dir) = await NewMachineAsync("watch");
        using var _ = profile;
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, Host, new StartOptions { Headless = true }, ct);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<PowerState>();
        await using var watcher = await MachineWatcher.AttachAsync(running, ct);
        watcher.StateChanged += (_, s) => seen.Enqueue(s);
        try
        {
            Assert.Equal(PowerState.Running, watcher.State);
            await MachineRunner.PauseAsync(running, ct);
            await MachineRunner.ResumeAsync(running, ct);
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }

        await watcher.Exited.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(PowerState.Off, watcher.State);
        Assert.Equal([PowerState.Paused, PowerState.Running], seen.Take(2));
        Assert.Equal(PowerState.Off, seen.Last());
    }

    /// <summary>
    /// The user's report (s7, "can't eject"): once the system is installed its installer stays out. The machine still
    /// has an empty drive (QEMU's default one when no disc is given), so a disc goes in and comes out while it runs.
    /// </summary>
    [Fact]
    public async Task An_installed_machine_keeps_an_empty_drive_a_disc_goes_in_and_comes_out_while_it_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var (profile, m, dir) = await NewMachineAsync("drive", x => x with
        {
            Media = [new MediaConfig { Role = MediaRole.Installer, File = "installer.iso", Connect = Connect.UntilInstalled }],
        });
        using var _ = profile;
        VmQontrol.Core.Media.IsoWriter.Write(Path.Combine(dir, "installer.iso"), "INSTALLER", new Dictionary<string, byte[]> { ["a.txt"] = "a"u8.ToArray() });
        var disc = Path.Combine(dir, "tools.iso");
        VmQontrol.Core.Media.IsoWriter.Write(disc, "TOOLS", new Dictionary<string, byte[]> { ["t.txt"] = "t"u8.ToArray() });
        // "Installed": the system disk holds more than an empty one does.
        File.Delete(Path.Combine(dir, "disk0.qcow2"));
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-o", "preallocation=metadata", Path.Combine(dir, "disk0.qcow2"), "1G");
        Assert.True(DiskState.IsUsed(Path.Combine(dir, "disk0.qcow2")), "the control: the disk must count as installed");

        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, Host, new StartOptions { Headless = true }, ct);
        await using var watcher = await MachineWatcher.AttachAsync(running, ct);
        try
        {
            var cmdline = await File.ReadAllTextAsync($"/proc/{running.Pid}/cmdline", ct);
            Assert.DoesNotContain("installer.iso", cmdline, StringComparison.Ordinal);
            var drives = await LiveMedia.DrivesAsync(watcher, ct);
            Assert.Contains(drives, d => d.File is null);

            await LiveMedia.ChangeAsync(watcher, null, disc, ct);
            Assert.Contains(await LiveMedia.DrivesAsync(watcher, ct), d => d.File == disc);
            await LiveMedia.EjectAsync(watcher, disc, ct);
            Assert.DoesNotContain(await LiveMedia.DrivesAsync(watcher, ct), d => d.File == disc);
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
        }
    }
}
