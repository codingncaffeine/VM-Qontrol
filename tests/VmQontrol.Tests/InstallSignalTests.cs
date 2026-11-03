using VmQontrol.Core.Install;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;

namespace VmQontrol.Tests;

/// <summary>Easy Install's "finished" signal: the port, the answer files' last step, and the stages the machine page shows.</summary>
public sealed class InstallSignalTests
{
    private static MachineConfig Easy(string os = "windows") => new()
    {
        Name = "Win",
        Guest = new GuestConfig { Os = os },
        Disks = [new DiskConfig { File = "disk0.qcow2" }],
        EasyInstall = new EasyInstallConfig { System = "Windows 11 Pro", UserName = "Ann" },
    };

    private static List<string> Args(MachineConfig m, string? signal) => QemuCommandBuilder.Build(m, new LaunchContext
    {
        Host = RecordedHost.Load(),
        MachineDirectory = "/vms/m",
        Runtime = new RuntimePaths("/run/vmq/m"),
        Headless = true,
        FileExists = _ => false,
        FirmwareFormat = _ => "raw",
        InstallSignal = signal,
    }).Args.ToList();

    [Fact]
    public void The_stages_run_from_not_started_through_installing_to_finished_and_seen()
    {
        using var profile = new TestProfile("signal");
        var dir = profile.Root;
        Assert.Equal(InstallStage.None, InstallSignal.Read(Easy() with { EasyInstall = null }, dir).Stage);
        Assert.Null(InstallSignal.Prepare(Easy() with { EasyInstall = null }, dir));
        Assert.Equal(InstallStage.NotStarted, InstallSignal.Read(Easy(), dir).Stage);

        var file = InstallSignal.Prepare(Easy(), dir);
        Assert.Equal(Path.Combine(dir, InstallSignal.FileName), file);
        var installing = InstallSignal.Read(Easy(), dir);
        Assert.Equal(InstallStage.Installing, installing.Stage);
        Assert.NotNull(installing.Started);
        // A second power on part way keeps the first start time.
        Assert.Equal(file, InstallSignal.Prepare(Easy(), dir));
        Assert.Equal(installing.Started, InstallSignal.Read(Easy(), dir).Started);

        // What the guest writes through the serial line (Windows' WriteLine ends in CR LF).
        File.AppendAllText(file!, InstallSignal.Marker + "\r\n");
        var done = InstallSignal.Read(Easy(), dir);
        Assert.Equal(InstallStage.Finished, done.Stage);
        Assert.False(done.Acknowledged);
        Assert.NotNull(done.Took);
        // Finished: no port at the next power on.
        Assert.Null(InstallSignal.Prepare(Easy(), dir));

        InstallSignal.Acknowledge(dir, done.Finished!.Value);
        var seen = InstallSignal.Read(Easy(), dir);
        Assert.True(seen.Acknowledged);
        Assert.Equal(done.Finished, seen.Finished);
    }

    [Fact]
    public void The_port_is_the_fourth_COM_port_appending_to_the_signal_file_and_only_when_asked()
    {
        Assert.DoesNotContain(Args(Easy(), null), a => a.Contains("installsignal", StringComparison.Ordinal));
        var args = Args(Easy(), "/vms/my, machine/install-signal.log");
        // Commas in the path are doubled, as QEMU's option syntax needs.
        Assert.Contains("file,id=installsignal,path=/vms/my,, machine/install-signal.log,append=on", args);
        Assert.Contains("isa-serial,chardev=installsignal,id=installsignal-port,index=3", args);
    }

    [Fact]
    public void The_port_is_left_out_when_the_four_ISA_ports_are_taken()
    {
        var m = Easy("linux") with { HostSerialPorts = [new() { Path = "/dev/ttyS0" }, new() { Path = "/dev/ttyS1" }, new() { Path = "/dev/ttyUSB0" }] };
        // Linux has the console on COM1, plus three host ports: no room.
        Assert.DoesNotContain(Args(m, "/vms/m/install-signal.log"), a => a.Contains("installsignal", StringComparison.Ordinal));
        Assert.Contains(Args(m with { HostSerialPorts = m.HostSerialPorts.Take(2).ToList() }, "/vms/m/install-signal.log"), a => a.Contains("index=3", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_answer_file_ends_by_writing_the_marker()
    {
        var xml = WindowsAnswerFile.Build(new WindowsInstallChoices { UserName = "Ann", ComputerName = "LAB", FirstLogonCommands = ["shutdown /s /t 15"] }, "11");
        var signal = xml.IndexOf("ACPI\\PNP0501\\4", StringComparison.Ordinal);
        Assert.True(signal > 0);
        // Before the person's own first-logon commands (a shutdown there must not beat it).
        Assert.True(signal < xml.IndexOf("shutdown /s /t 15", StringComparison.Ordinal));
        Assert.Contains(InstallSignal.Marker, xml, StringComparison.Ordinal);

        var linux = new LinuxInstallChoices { UserName = "Ann", Password = "x", HostName = "lab" };
        Assert.Contains("late-commands:\n    - sh -c 'echo VMQONTROL-INSTALLED > /dev/ttyS3'", LinuxEasyInstall.UbuntuAutoinstall(linux, "h").ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("%post --nochroot\necho VMQONTROL-INSTALLED > /dev/ttyS3\n%end", LinuxEasyInstall.Kickstart(linux, "h").ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("d-i preseed/late_command string echo VMQONTROL-INSTALLED > /dev/ttyS3", LinuxEasyInstall.Preseed(linux, "h"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_factory_records_what_Easy_Install_was_asked_and_a_plain_machine_records_nothing()
    {
        using var profile = new TestProfile("signal-factory");
        var (m, _) = MachineFactory.Create(new NewMachineRequest
        {
            Name = "Win",
            LibraryRoot = profile.Paths.DefaultLibrary,
            Guest = new GuestConfig { Os = "windows", Release = "11" },
            InstallerIso = "/isos/win11.iso",
            WindowsEasyInstall = new WindowsInstallChoices { UserName = "Ann", ComputerName = "WIN", EditionName = "Windows 11 Pro", Lean = true },
        });
        Assert.Equal(new EasyInstallConfig { System = "Windows 11 Pro", UserName = "Ann", Lean = true }, m.EasyInstall);
        // It survives the save (vm.json) and shows in the settings history.
        var loaded = MachineStore.Load(Path.Combine(profile.Paths.DefaultLibrary, "Win", MachineStore.FileName));
        Assert.Equal(m.EasyInstall, loaded.EasyInstall);
        Assert.Contains(SettingsHistory.Describe(loaded, loaded with { EasyInstall = null }), c => c.StartsWith("Easy Install", StringComparison.Ordinal));

        var (plain, _) = MachineFactory.Create(new NewMachineRequest { Name = "Plain", LibraryRoot = profile.Paths.DefaultLibrary, Guest = new GuestConfig() });
        Assert.Null(plain.EasyInstall);
    }
}
