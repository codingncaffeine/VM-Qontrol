using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

public sealed class PerformanceTests
{
    private static IReadOnlyList<string> Args(MachineConfig m) =>
        QemuCommandBuilder.Build(m, new LaunchContext { Host = RecordedHost.Load(), MachineDirectory = "/m", Runtime = new RuntimePaths("/r"), Headless = true }).Args;

    private static MachineConfig Machine() => new() { Name = "p", MemoryMiB = 4096, Disks = [new DiskConfig { File = "d.qcow2" }, new DiskConfig { File = "e.qcow2" }] };

    [Fact]
    public void Nothing_changes_until_a_performance_setting_is_chosen()
    {
        var args = string.Join(' ', Args(Machine()));
        Assert.DoesNotContain("iothread", args, StringComparison.Ordinal);
        Assert.DoesNotContain("hugetlb", args, StringComparison.Ordinal);
        Assert.DoesNotContain("ivshmem", args, StringComparison.Ordinal);
        Assert.DoesNotContain("vfio-pci", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_VirtIO_disk_gets_its_own_IO_thread()
    {
        var m = Machine();
        m.Performance.IoThreads = true;
        var args = Args(m).ToList();
        Assert.Equal(["iothread,id=io0", "iothread,id=io1"], args.Where(a => a.StartsWith("iothread,", StringComparison.Ordinal)));
        Assert.Contains(args, a => a.StartsWith("virtio-blk-pci,drive=SystemDisk", StringComparison.Ordinal) && a.EndsWith(",iothread=io0", StringComparison.Ordinal));
        Assert.Contains(args, a => a.StartsWith("virtio-blk-pci,drive=Disk1", StringComparison.Ordinal) && a.EndsWith(",iothread=io1", StringComparison.Ordinal));
        Assert.True(args.IndexOf("iothread,id=io0") < args.FindIndex(a => a.Contains("iothread=io0", StringComparison.Ordinal)), "the thread is made before the disk names it");
    }

    [Fact]
    public void Huge_pages_back_the_whole_memory_and_join_shared_folders_memory()
    {
        var m = Machine();
        m.Performance.HugePages = "1G";
        Assert.Contains("memory-backend-memfd,id=mem,size=4G,hugetlb=on,hugetlbsize=1G", Args(m));
        m.Shares.Add(new ShareConfig { Path = "/tmp", Tag = "t" });
        var args = Args(m);
        Assert.Contains("memory-backend-memfd,id=mem,size=4G,share=on,hugetlb=on,hugetlbsize=1G", args);
        Assert.Single(args, a => a.StartsWith("memory-backend-memfd", StringComparison.Ordinal));
    }

    [Fact]
    public void Looking_Glass_and_PCI_devices_are_on_the_command_line()
    {
        var m = Machine();
        m.Performance.LookingGlassMiB = 64;
        m.Performance.PciDevices = ["0000:0b:00.0", "0000:0b:00.1"];
        var args = Args(m);
        Assert.Contains($"memory-backend-file,id=looking-glass,share=on,mem-path={QemuCommandBuilder.LookingGlassFile},size=64M", args);
        Assert.Contains("ivshmem-plain,memdev=looking-glass", args);
        Assert.Contains("vfio-pci,host=0000:0b:00.0,id=hostpci0", args);
        Assert.Contains("vfio-pci,host=0000:0b:00.1,id=hostpci1", args);
    }

    private static void Cpu(string sys, int id, int core, int die, string siblings, string l3, string size)
    {
        var d = Path.Combine(sys, $"cpu{id}");
        Directory.CreateDirectory(Path.Combine(d, "topology"));
        Directory.CreateDirectory(Path.Combine(d, "cache", "index3"));
        File.WriteAllText(Path.Combine(d, "topology", "core_id"), core + "\n");
        File.WriteAllText(Path.Combine(d, "topology", "die_id"), die + "\n");
        File.WriteAllText(Path.Combine(d, "topology", "thread_siblings_list"), siblings + "\n");
        File.WriteAllText(Path.Combine(d, "cache", "index3", "shared_cpu_list"), l3 + "\n");
        File.WriteAllText(Path.Combine(d, "cache", "index3", "size"), size + "\n");
    }

    /// <summary>A two-CCD part like the 9950X3D (16 cores, one CCD with the large cache): pinning goes to the large cache, whole cores, core 0 left to the host.</summary>
    [Fact]
    public void Pinning_is_recommended_inside_the_largest_cache_with_whole_cores()
    {
        using var profile = new TestProfile("topology");
        var sys = Path.Combine(profile.Root, "cpu");
        for (var i = 0; i < 32; i++)
        {
            var core = i % 16;
            var first = core < 8;
            Cpu(sys, i, core, first ? 0 : 1, $"{core},{core + 16}", first ? "0-7,16-23" : "8-15,24-31", first ? "98304K" : "32768K");
        }

        var cpus = CpuTopology.List(sys);
        Assert.Equal(32, cpus.Count);
        var groups = CpuTopology.Groups(cpus);
        Assert.Equal(2, groups.Count);
        Assert.Equal("8 cores, 16 threads, 96 MB L3 (the largest cache)", groups[0].Describe(true));
        Assert.Equal([1, 17, 2, 18, 3, 19, 4, 20], CpuTopology.Recommend(cpus, 8));
        Assert.Equal([0, 16, 1, 17, 2, 18, 3, 19, 4, 20, 5, 21, 6, 22, 7, 23], CpuTopology.Recommend(cpus, 16)); // the whole CCD: no core left to spare
        Assert.Null(CpuTopology.Recommend(cpus, 20));
        Assert.Equal("0-7,16-23", CpuTopology.FormatList(CpuTopology.ParseList("16-23,0-7")));
    }

    /// <summary>
    /// A real QEMU with two processors pinned to this computer's 2 and 3, and its other threads to 4: the kernel's own
    /// record (/proc/PID/task/TID/status) says so. I/O threads and Looking Glass's memory start with it.
    /// </summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task Pinning_IO_threads_and_Looking_Glass_memory_on_a_running_machine()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(Environment.ProcessorCount < 5, "needs five processors");
        using var profile = new TestProfile("pinning");
        var glFile = $"/dev/shm/vmq-test-looking-glass-{Guid.NewGuid():N}";
        QemuCommandBuilder.LookingGlassFile = glFile;
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "pin");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig
        {
            Name = "pin", MemoryMiB = 256, Cpu = new CpuConfig { Cores = 2 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
            Performance = new PerformanceConfig { PinnedCpus = [2, 3], EmulatorCpus = [4], IoThreads = true, LookingGlassMiB = 32 },
        };
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            Assert.Empty(running.Notices);
            await using var qmp = await MachineRunner.ControlAsync(running, ct);
            var vcpus = (System.Text.Json.Nodes.JsonArray)(await qmp.ExecuteAsync("query-cpus-fast", ct: ct))!;
            var tids = vcpus.Select(v => v!["thread-id"]!.GetValue<int>()).ToList();
            Assert.Equal([2], CpuPinning.Allowed(running.Pid, tids[0]));
            Assert.Equal([3], CpuPinning.Allowed(running.Pid, tids[1]));
            Assert.Equal([4], CpuPinning.Allowed(running.Pid, running.Pid)); // QEMU's main thread
            var iothreads = (System.Text.Json.Nodes.JsonArray)(await qmp.ExecuteAsync("query-iothreads", ct: ct))!;
            Assert.Equal("io0", Assert.Single(iothreads)!["id"]!.GetValue<string>());
            Assert.Equal(32L << 20, new FileInfo(glFile).Length);
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
            QemuCommandBuilder.LookingGlassFile = "/dev/shm/looking-glass";
            File.Delete(glFile);
        }
    }
}
