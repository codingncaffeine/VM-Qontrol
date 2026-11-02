using VmQontrol.Core.Hosts;

namespace VmQontrol.Tests;

public sealed class FirmwareLocatorTests
{
    private static HashSet<string> Files(params string[] f) => f.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Arch_edk2_layout()
    {
        var present = Files("/usr/share/edk2/x64/OVMF_CODE.4m.fd", "/usr/share/edk2/x64/OVMF_VARS.4m.fd", "/usr/share/edk2/x64/OVMF_CODE.secboot.4m.fd");
        Assert.Equal("/usr/share/edk2/x64/OVMF_CODE.4m.fd", FirmwareLocator.Find(present, secureBoot: false)!.Code);
        var secure = FirmwareLocator.Find(present, secureBoot: true)!;
        Assert.Equal("/usr/share/edk2/x64/OVMF_CODE.secboot.4m.fd", secure.Code);
        Assert.Equal("/usr/share/edk2/x64/OVMF_VARS.4m.fd", secure.VarsTemplate);
    }

    [Fact]
    public void Debian_and_Ubuntu_layout_with_Microsoft_keys_for_secure_boot()
    {
        var present = Files("/usr/share/OVMF/OVMF_CODE_4M.fd", "/usr/share/OVMF/OVMF_VARS_4M.fd", "/usr/share/OVMF/OVMF_CODE_4M.secboot.fd", "/usr/share/OVMF/OVMF_VARS_4M.ms.fd");
        Assert.Equal("/usr/share/OVMF/OVMF_VARS_4M.fd", FirmwareLocator.Find(present, false)!.VarsTemplate);
        Assert.Equal("/usr/share/OVMF/OVMF_VARS_4M.ms.fd", FirmwareLocator.Find(present, true)!.VarsTemplate);
    }

    [Fact]
    public void Fedora_layout()
    {
        var present = Files("/usr/share/edk2/ovmf/OVMF_CODE.fd", "/usr/share/edk2/ovmf/OVMF_VARS.fd", "/usr/share/edk2/ovmf/OVMF_CODE.secboot.fd", "/usr/share/edk2/ovmf/OVMF_VARS.secboot.fd");
        Assert.Equal("/usr/share/edk2/ovmf/OVMF_CODE.secboot.fd", FirmwareLocator.Find(present, true)!.Code);
    }

    [Fact]
    public void Alpine_has_firmware_but_no_secure_boot_build()
    {
        var present = Files("/usr/share/OVMF/OVMF.fd", "/usr/share/OVMF/OVMF_CODE.fd", "/usr/share/OVMF/OVMF_VARS.fd");
        Assert.Equal("/usr/share/OVMF/OVMF_CODE.fd", FirmwareLocator.Find(present, false)!.Code);
        Assert.Null(FirmwareLocator.Find(present, true));
    }

    [Fact]
    public void Like_quickemu_the_last_pair_present_wins()
    {
        var present = Files("/usr/share/OVMF/OVMF_CODE_4M.fd", "/usr/share/OVMF/OVMF_VARS_4M.fd", "/usr/share/edk2/x64/OVMF_CODE.4m.fd", "/usr/share/edk2/x64/OVMF_VARS.4m.fd");
        Assert.Equal("/usr/share/edk2/x64/OVMF_CODE.4m.fd", FirmwareLocator.Find(present, false)!.Code);
    }
}
