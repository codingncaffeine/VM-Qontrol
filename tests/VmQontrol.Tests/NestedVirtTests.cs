using System.Text;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Phase 5 audit: the nested virtualization switch (Workstation's VT-x/AMD-V box).</summary>
public sealed class NestedVirtTests
{
    private static QemuLaunch Build(MachineConfig m, HostCapabilities host) => QemuCommandBuilder.Build(m, new LaunchContext
    {
        Host = host,
        MachineDirectory = "/vms/m",
        Runtime = new RuntimePaths("/run/vmq/m"),
        FileExists = _ => false,
        FirmwareFormat = _ => "raw",
    });

    private static string Cpu(QemuLaunch l)
    {
        var args = l.Args.ToList();
        return args[args.IndexOf("-cpu") + 1];
    }

    private static MachineConfig Linux(bool? nested, string? model = null) =>
        new() { Name = "T", Cpu = new CpuConfig { Nested = nested, Model = model }, Disks = [new DiskConfig { File = "disk0.qcow2" }] };

    private static HostCapabilities Amd => RecordedHost.Load() with { CpuVendor = "AuthenticAMD", KvmUsable = true, NestedKvm = true };

    private static HostCapabilities Intel => RecordedHost.Load() with { CpuVendor = "GenuineIntel", KvmUsable = true, NestedKvm = true };

    [Fact]
    public void Default_keeps_quickemus_line_off_hides_the_flag_on_adds_it_to_a_named_model()
    {
        var plain = Cpu(Build(Linux(null), Amd));
        Assert.DoesNotContain("svm", plain, StringComparison.Ordinal);
        Assert.Equal(plain + ",-svm", Cpu(Build(Linux(false), Amd)));
        Assert.Equal(plain, Cpu(Build(Linux(true), Amd)));
        Assert.Equal(Cpu(Build(Linux(null), Intel)) + ",-vmx", Cpu(Build(Linux(false), Intel)));
        Assert.EndsWith(",+vmx", Cpu(Build(Linux(true, "Haswell-v4"), Intel)), StringComparison.Ordinal);
        // Windows' line starts with host too: nothing to add when on.
        var win = new MachineConfig { Name = "W", Guest = new GuestConfig { Os = "windows", Release = "11" }, Cpu = new CpuConfig { Nested = true }, Disks = [new DiskConfig { File = "disk0.qcow2" }] };
        Assert.DoesNotContain("svm", Cpu(Build(win, Amd)), StringComparison.Ordinal);
    }

    [Fact]
    public void On_says_so_when_this_computers_kvm_keeps_it_off()
    {
        const string Says = "Nested virtualization is off in this computer's KVM";
        Assert.DoesNotContain(Build(Linux(true), Amd).Warnings, w => w.StartsWith(Says, StringComparison.Ordinal));
        Assert.Contains(Build(Linux(true), Amd with { NestedKvm = false }).Warnings, w => w.StartsWith(Says, StringComparison.Ordinal));
        Assert.DoesNotContain(Build(Linux(null), Amd with { NestedKvm = false }).Warnings, w => w.StartsWith(Says, StringComparison.Ordinal));
    }

    [Fact]
    public void The_kernel_parameter_is_read_both_ways()
    {
        var root = Path.Combine(TestProfile.ScratchRoot, "nested-sys-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "kvm_amd", "parameters"));
            File.WriteAllText(Path.Combine(root, "kvm_amd", "parameters", "nested"), "1\n");
            Assert.True(NestedVirt.Read("kvm_amd", root));
            File.WriteAllText(Path.Combine(root, "kvm_amd", "parameters", "nested"), "0\n");
            Assert.False(NestedVirt.Read("kvm_amd", root));
            File.WriteAllText(Path.Combine(root, "kvm_amd", "parameters", "nested"), "Y\n");
            Assert.True(NestedVirt.Read("kvm_amd", root));
            Assert.Null(NestedVirt.Read("kvm_intel", root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_change_is_in_the_history()
    {
        var before = Linux(null);
        Assert.Contains("Nested virtualization: as this computer allows → off", SettingsHistory.Describe(before, Linux(false)), StringComparer.Ordinal);
    }

    [Trait("Tier", "Guest")]
    [Fact]
    public async Task A_guest_sees_the_feature_unless_it_is_switched_off()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        var host = HostProbe.Probe();
        Assert.SkipWhen(host.NestedKvm != true, "This computer's KVM has nested virtualization off.");
        var flag = NestedVirt.Flag(host);
        using var profile = new TestProfile("nested");
        // Both at once: the control (as this computer allows) and the switch turned off.
        var results = await Task.WhenAll(Boot(null), Boot(false));
        Assert.True(results[0], $"the control guest lacks {flag}: the check cannot see the switch");
        Assert.False(results[1], $"the guest still sees {flag} with nested virtualization off");

        async Task<bool> Boot(bool? nested)
        {
            var name = nested is null ? "control" : "off";
            var dir = Path.Combine(profile.Paths.DefaultLibrary, name);
            Directory.CreateDirectory(dir);
            await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
            IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
            {
                ["meta-data"] = Encoding.UTF8.GetBytes($"instance-id: vmq-nested-{name}\nlocal-hostname: vmq-{name}\n"),
                ["user-data"] = Encoding.UTF8.GetBytes("""
                    #cloud-config
                    packages: [qemu-guest-agent]
                    runcmd:
                      - rc-update add qemu-guest-agent default
                      - rc-service qemu-guest-agent start
                    """),
            });
            var m = new MachineConfig
            {
                Name = name,
                MemoryMiB = 512,
                Cpu = new CpuConfig { Cores = 1, Nested = nested },
                Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
                Disks = [new DiskConfig { File = "disk0.qcow2" }],
                Media = [new MediaConfig { Role = MediaRole.Drivers, File = "seed.iso" }],
            };
            MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
            var running = await MachineRunner.StartAsync(m, dir, profile.Paths, host, new StartOptions { Headless = true }, ct);
            try
            {
                var agent = new GuestAgentClient(running.Paths.Agent);
                var deadline = DateTime.UtcNow.AddMinutes(4);
                while (!await agent.PingAsync(TimeSpan.FromSeconds(2), ct))
                {
                    Assert.True(DateTime.UtcNow < deadline, "the guest agent never answered (cloud-init installs it from the network at first boot)");
                    await Task.Delay(2000, ct);
                }

                var cpuinfo = Encoding.ASCII.GetString(await agent.ReadFileAsync("/proc/cpuinfo", TimeSpan.FromSeconds(3), ct) ?? []);
                Assert.Contains("flags", cpuinfo, StringComparison.Ordinal);
                return cpuinfo.Split('\n').First(l => l.StartsWith("flags", StringComparison.Ordinal)).Split(' ').Contains(flag);
            }
            finally
            {
                await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            }
        }
    }
}
