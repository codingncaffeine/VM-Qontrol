using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using VmQontrol.Cli;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>vmq support: everything a helper needs, nothing personal, and never a disk or an answer file.</summary>
public sealed class SupportBundleTests
{
    [Fact]
    public void The_bundle_has_the_logs_and_settings_and_none_of_the_personal_parts()
    {
        using var profile = new TestProfile("support");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var user = Environment.UserName;
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Work");
        Directory.CreateDirectory(dir);
        var m = new MachineConfig
        {
            Name = "Work",
            Notes = "my bank PIN is in here",
            Guest = new GuestConfig { Os = "linux", Account = "secretaccount" },
            EasyInstall = new EasyInstallConfig { System = "Ubuntu", UserName = "secretaccount" },
            Disks = [new DiskConfig { File = Path.Combine(home, "disk0.qcow2") }],
        };
        MachineStore.Save(m, Path.Combine(dir, "vm.json"));
        File.WriteAllText(Path.Combine(dir, "disk0.qcow2"), "disk");
        File.WriteAllText(Path.Combine(dir, "autounattend.xml"), "<Password>hunter2</Password>");
        var logs = MachineRunner.LogDirectory(profile.Paths, m);
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "qemu.log"), $"qemu-system-x86_64: could not open {home}/thing for {user}\n");
        profile.Paths.EnsureCreated();
        File.WriteAllText(Path.Combine(profile.Paths.Logs, "vmqontrol.log"), $"started by {user} in {home}\n");

        var bundle = Path.Combine(profile.Root, "support.tar.gz");
        var output = new StringWriter();
        Assert.Equal(0, Commands.Run(["support", bundle], output, new StringWriter(), profile.Paths, () => HostProbe.Probe()));
        Assert.StartsWith($"Saved {bundle}", output.ToString(), StringComparison.Ordinal);

        var files = new Dictionary<string, string>();
        using (var gz = new GZipStream(File.OpenRead(bundle), CompressionMode.Decompress))
        using (var tar = new TarReader(gz))
            while (tar.GetNextEntry() is { } e)
                files[e.Name] = new StreamReader(e.DataStream!, Encoding.UTF8).ReadToEnd();

        Assert.Contains("vmqontrol-support/system.txt", files.Keys);
        Assert.Contains("vmqontrol-support/doctor.txt", files.Keys);
        Assert.Contains("QEMU: ", files["vmqontrol-support/system.txt"], StringComparison.Ordinal);
        Assert.Contains("could not open ~/thing for <user>", files["vmqontrol-support/machines/Work/logs/qemu.log"], StringComparison.Ordinal);
        Assert.Contains("started by <user> in ~", files["vmqontrol-support/logs/vmqontrol.log"], StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Work\"", files["vmqontrol-support/machines/Work/vm.json"].Replace("\":\"", "\": \"", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("~/disk0.qcow2", files["vmqontrol-support/machines/Work/vm.json"], StringComparison.Ordinal);
        // Only text the bundle names: no disk, no answer file, nothing else from the machine folder.
        Assert.All(files.Keys, k => Assert.Matches(@"^vmqontrol-support/(system\.txt|doctor\.txt|logs/[^/]+\.log|machines/[^/]+/(vm\.json|logs/[^/]+\.log))$", k));
        var all = string.Join("\n", files.Values);
        Assert.DoesNotContain("bank PIN", all, StringComparison.Ordinal);
        Assert.DoesNotContain("secretaccount", all, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", all, StringComparison.Ordinal);
        Assert.DoesNotContain(home + "/", all, StringComparison.Ordinal);
        if (user.Length >= 3) Assert.DoesNotMatch($@"(?<![A-Za-z0-9]){System.Text.RegularExpressions.Regex.Escape(user)}(?![A-Za-z0-9])", all);
        // Control: the planted personal parts were really in the sources the bundle read.
        Assert.Contains("secretaccount", File.ReadAllText(Path.Combine(dir, "vm.json")), StringComparison.Ordinal);
        Assert.Contains(home, File.ReadAllText(Path.Combine(logs, "qemu.log")), StringComparison.Ordinal);
    }
}
