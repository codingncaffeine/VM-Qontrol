using VmQontrol.Core.Hosts;

namespace VmQontrol.Tests;

public sealed class DoctorTests
{
    private static IReadOnlyList<DoctorCheck> Run(HostCapabilities host, DistroFamily distro, params string[] onPath) =>
        Doctor.Run(host, distro, "/vms", n => onPath.Contains(n), _ => false, _ => 500L * 1024 * 1024 * 1024);

    [Theory]
    [InlineData("ID=\"endeavouros\"\nID_LIKE=\"arch\"\n", DistroFamily.Arch)]
    [InlineData("ID=ubuntu\nID_LIKE=debian\n", DistroFamily.Debian)]
    [InlineData("ID=linuxmint\nID_LIKE=\"ubuntu debian\"\n", DistroFamily.Debian)]
    [InlineData("ID=fedora\n", DistroFamily.Fedora)]
    [InlineData("ID=alpine\n", DistroFamily.Alpine)]
    [InlineData("ID=opensuse-tumbleweed\nID_LIKE=\"opensuse suse\"\n", DistroFamily.OpenSuse)]
    public void The_distribution_family_comes_from_os_release(string text, DistroFamily family) => Assert.Equal(family, Doctor.DetectDistro(text));

    [Fact]
    public void A_missing_TPM_names_the_right_package_per_distribution()
    {
        var host = RecordedHost.Load();
        Assert.Equal("sudo pacman -S --needed swtpm", Run(host, DistroFamily.Arch).Single(c => c.Title.Contains("TPM", StringComparison.Ordinal)).Fix);
        Assert.Equal("sudo apt install swtpm swtpm-tools", Run(host, DistroFamily.Debian).Single(c => c.Title.Contains("TPM", StringComparison.Ordinal)).Fix);
        Assert.Equal("sudo apk add swtpm", Run(host, DistroFamily.Alpine).Single(c => c.Title.Contains("TPM", StringComparison.Ordinal)).Fix);
    }

    [Fact]
    public void Without_KVM_access_the_fix_is_the_kvm_group()
    {
        var checks = Doctor.Run(RecordedHost.Load() with { KvmUsable = false }, DistroFamily.Arch, "/vms", _ => true, p => p == "/dev/kvm", _ => null);
        var kvm = checks.Single(c => c.Title.Contains("KVM", StringComparison.Ordinal));
        Assert.Equal(CheckStatus.Warning, kvm.Status);
        Assert.Equal("sudo usermod -aG kvm $USER", kvm.Fix);
    }

    [Fact]
    public void Alpine_is_told_plainly_that_secure_boot_firmware_does_not_exist_there()
    {
        var host = RecordedHost.Load() with { FirmwareFiles = new HashSet<string> { "/usr/share/OVMF/OVMF_CODE.fd", "/usr/share/OVMF/OVMF_VARS.fd" } };
        var secure = Run(host, DistroFamily.Alpine).Single(c => c.Title == "Secure Boot firmware");
        Assert.Equal(CheckStatus.Optional, secure.Status);
        Assert.Null(secure.Fix);
        Assert.Contains("no Secure Boot build", secure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_host_has_nothing_missing() =>
        Assert.DoesNotContain(Run(RecordedHost.Load(), DistroFamily.Arch, "qemu-img", "swtpm", "remote-viewer", "passt", "virtiofsd", "quickget"), c => c.Status == CheckStatus.Missing);
}
