using System.Diagnostics;
using VmQontrol.Core.Downloads;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>D39: ready-made Linux machines from the distributions' cloud images.</summary>
public sealed class CloudImageTests
{
    [Fact]
    public void Published_checksum_lists_are_read_in_each_distributions_format()
    {
        var hex = new string('a', 64);
        Assert.Equal(hex, CloudImages.SumsLine($"{new string('b', 64)} *other.img\n{hex} *ubuntu-26.04-server-cloudimg-amd64.img\n", "ubuntu-26.04-server-cloudimg-amd64.img"));
        Assert.Equal(hex, CloudImages.SumsLine($"{hex}  debian-13-genericcloud-amd64.qcow2\n", "debian-13-genericcloud-amd64.qcow2"));
        Assert.Equal(hex, CloudImages.SumsLine($"{hex}\n", "alpine.qcow2", alone: true));
        Assert.Throws<DownloadFailedException>(() => CloudImages.SumsLine($"{hex} *other.img\n", "mine.img"));
    }

    [Fact]
    public void The_cloud_config_makes_the_account_with_a_key_and_password_or_key_only_without_a_sudo_password()
    {
        var both = CloudImages.UserData(new CloudChoices { Name = "Web", UserName = "Ada", Password = "pw", SshKeys = ["ssh-ed25519 AAAA ada@host"] }, "ada");
        Assert.StartsWith("#cloud-config\nusers:\n  - name: ada\n    sudo: \"ALL=(ALL) ALL\"\n    lock_passwd: false\n    passwd: \"$6$", both, StringComparison.Ordinal);
        Assert.Contains("      - \"ssh-ed25519 AAAA ada@host\"", both, StringComparison.Ordinal);
        Assert.Contains("ssh_pwauth: true", both, StringComparison.Ordinal);
        Assert.Contains("packages: [qemu-guest-agent, sudo]", both, StringComparison.Ordinal);
        Assert.DoesNotContain("groups", both, StringComparison.Ordinal); // a missing group makes useradd fail outright

        var keyOnly = CloudImages.UserData(new CloudChoices { Name = "Web", UserName = "ada", SshKeys = ["ssh-ed25519 AAAA"] }, "ada");
        Assert.Contains("sudo: \"ALL=(ALL) NOPASSWD:ALL\"", keyOnly, StringComparison.Ordinal);
        Assert.Contains("lock_passwd: true", keyOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("passwd: \"", keyOnly, StringComparison.Ordinal);
        Assert.Contains("ssh_pwauth: false", keyOnly, StringComparison.Ordinal);
    }

    /// <summary>The seed rides on a read-only VirtIO disk, never a CD: Debian 12's cloud kernel has no SATA driver and
    /// never saw it on a CD (the machine came up as "localhost" with no account).</summary>
    [Fact]
    public void The_cloud_init_seed_is_a_read_only_virtio_disk()
    {
        var m = new MachineConfig { Name = "Web", Disks = [new DiskConfig { File = "disk0.qcow2" }], Media = [new MediaConfig { Role = MediaRole.CloudInit, File = CloudImages.SeedFile }] };
        var launch = VmQontrol.Core.Qemu.QemuCommandBuilder.Build(m, new VmQontrol.Core.Qemu.LaunchContext
        {
            Host = RecordedHost.Load(), MachineDirectory = "/vms/web", Runtime = new VmQontrol.Core.Qemu.RuntimePaths("/run/vmq/web"), FileExists = _ => false, FirmwareFormat = _ => "raw",
        });
        var args = string.Join(' ', launch.Args);
        Assert.Contains("-device virtio-blk-pci,drive=CloudInit -drive id=CloudInit,if=none,format=raw,readonly=on,file=/vms/web/cloud-init.iso", args, StringComparison.Ordinal);
        Assert.DoesNotContain("media=cdrom", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_public_keys_in_ssh_are_offered()
    {
        using var profile = new TestProfile("cloud-keys");
        var ssh = Path.Combine(profile.Root, ".ssh");
        Directory.CreateDirectory(ssh);
        File.WriteAllText(Path.Combine(ssh, "id_ed25519.pub"), "ssh-ed25519 AAAA me@here\n");
        File.WriteAllText(Path.Combine(ssh, "id_ed25519"), "-----BEGIN OPENSSH PRIVATE KEY-----\n");
        File.WriteAllText(Path.Combine(ssh, "notes.pub"), "not a key\n");
        Assert.Equal(["ssh-ed25519 AAAA me@here"], CloudImages.LocalPublicKeys(profile.Root));
    }

    public static TheoryData<string> Images => [.. CloudImages.All.Select(i => i.Id)];

    /// <summary>
    /// The whole path for one image, set VMQONTROL_CLOUD_PROOF="ubuntu-26.04 debian-13 …" (or "all"): today's file from
    /// the distribution, its checksum, the machine made and booted, then SSH with a fresh key: bash where it exists, sudo
    /// with the password, the disk grown to 20 GB, the guest agent answering. Images are kept in scratch/images/cloud.
    /// </summary>
    [Trait("Tier", "Guest")]
    [Theory]
    [MemberData(nameof(Images))]
    public async Task A_cloud_image_becomes_a_machine_you_can_ssh_into(string id)
    {
        var wanted = Environment.GetEnvironmentVariable("VMQONTROL_CLOUD_PROOF") ?? "";
        Assert.SkipWhen(wanted != "all" && !wanted.Split(' ').Contains(id), "set VMQONTROL_CLOUD_PROOF to run it (downloads the image)");
        var ct = TestContext.Current.CancellationToken;
        var image = CloudImages.All.Single(i => i.Id == id);
        using var profile = new TestProfile("cloud-" + id);
        CloudImages.CacheOverride = Path.Combine(TestProfile.ScratchRoot, "images", "cloud");
        var key = Path.Combine(profile.Root, "key");
        using (var gen = Process.Start("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", key])!) await gen.WaitForExitAsync(ct);
        const string password = "Qontrol-proof-7";

        var resolved = await CloudImages.ResolveAsync(image, ct);
        var file = await CloudImages.GetAsync(profile.Paths, resolved, new Progress<DownloadProgress>(), ct);
        var (m, dir) = await CloudImages.CreateAsync(profile.Paths.DefaultLibrary, image, file, new CloudChoices
        {
            Name = "cloud " + id, UserName = "Ada", Password = password, SshKeys = [File.ReadAllText(key + ".pub").Trim()], MemoryMiB = 2048,
        }, ct);
        Assert.Equal("ada", m.Guest.Account);
        var watch = Stopwatch.StartNew();
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            var agent = new GuestAgentClient(running.Paths.Agent);
            var deadline = DateTime.UtcNow.AddMinutes(6);
            string output = "";
            while (true)
            {
                Assert.True(DateTime.UtcNow < deadline, $"no SSH sign-in within 6 minutes; last: {output}");
                var ssh = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
                foreach (var a in new[] { "-F", "/dev/null", "-i", key, "-p", running.SshPort!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", "StrictHostKeyChecking=no",
                             "-o", "UserKnownHostsFile=/dev/null", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", "ada@localhost",
                             $"cloud-init status --wait >/dev/null 2>&1; echo shell=$(getent passwd ada | cut -d: -f7); echo {password} | sudo -S -p '' id -u; df -k / | tail -1; . /etc/os-release; echo id=$ID" })
                    ssh.ArgumentList.Add(a);
                using var p = Process.Start(ssh)!;
                output = (await p.StandardOutput.ReadToEndAsync(ct)) + (await p.StandardError.ReadToEndAsync(ct));
                await p.WaitForExitAsync(ct);
                if (p.ExitCode == 0 && output.Contains("id=", StringComparison.Ordinal)) break;
                Assert.DoesNotContain("Bad owner or permissions", output, StringComparison.Ordinal); // this computer's ssh, not the guest
                await Task.Delay(5000, ct);
            }

            TestContext.Current.SendDiagnosticMessage($"{id}: signed in after {watch.Elapsed.TotalSeconds:0} s from {resolved.FileName}:\n{output}");
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains("0", lines); // sudo with the password
            Assert.Contains(lines, l => l == (id == "alpine" ? "shell=/bin/sh" : "shell=/bin/bash") || l.StartsWith("shell=/bin/ash", StringComparison.Ordinal));
            var sizeKb = long.Parse(lines.First(l => l.StartsWith('/')).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(sizeKb > 15L * 1024 * 1024, $"the root filesystem was not grown: {sizeKb} KB");
            Assert.Contains(lines, l => l == $"id={(id.StartsWith("ubuntu", StringComparison.Ordinal) ? "ubuntu" : id.StartsWith("debian", StringComparison.Ordinal) ? "debian" : id)}");
            Assert.True(await agent.PingAsync(TimeSpan.FromSeconds(5), ct), "the guest agent does not answer");
        }
        finally
        {
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
            CloudImages.CacheOverride = null;
        }
    }
}
