namespace VmQontrol.Core.Hosts;

/// <summary>A UEFI firmware code file and the variable-store template that goes with it.</summary>
public sealed record FirmwarePair(string Code, string VarsTemplate, bool SecureBoot);

/// <summary>
/// Finds the host's OVMF firmware. The candidate list is quickemu 4.9.9's (Arch, Debian/Ubuntu, Fedora, openSUSE, Nix,
/// QEMU's own) plus Alpine's, and like quickemu the LAST pair present wins.
/// </summary>
public static class FirmwareLocator
{
    private static readonly (string Code, string Vars)[] SecureBootPairs =
    [
        ("OVMF/OVMF_CODE_4M.secboot.fd", "OVMF/OVMF_VARS_4M.ms.fd"),
        ("edk2/ovmf/OVMF_CODE.secboot.fd", "edk2/ovmf/OVMF_VARS.secboot.fd"),
        ("OVMF/x64/OVMF_CODE.secboot.fd", "OVMF/x64/OVMF_VARS.fd"),
        ("edk2-ovmf/OVMF_CODE.secboot.fd", "edk2-ovmf/OVMF_VARS.fd"),
        ("qemu/ovmf-x86_64-smm-ms-code.bin", "qemu/ovmf-x86_64-smm-ms-vars.bin"),
        ("qemu/edk2-x86_64-secure-code.fd", "qemu/edk2-x86_64-code.fd"),
        ("edk2-ovmf/x64/OVMF_CODE.secboot.fd", "edk2-ovmf/x64/OVMF_VARS.fd"),
        ("edk2/x64/OVMF_CODE.secboot.4m.fd", "edk2/x64/OVMF_VARS.4m.fd"),
        ("edk2/ovmf/OVMF_CODE_4M.secboot.qcow2", "edk2/ovmf/OVMF_VARS_4M.secboot.qcow2"),
    ];

    private static readonly (string Code, string Vars)[] PlainPairs =
    [
        ("OVMF/OVMF_CODE_4M.fd", "OVMF/OVMF_VARS_4M.fd"),
        ("edk2/ovmf/OVMF_CODE.fd", "edk2/ovmf/OVMF_VARS.fd"),
        // Alpine's ovmf package (and older Debian) put the plain build here.
        ("OVMF/OVMF_CODE.fd", "OVMF/OVMF_VARS.fd"),
        ("OVMF/x64/OVMF_CODE.fd", "OVMF/x64/OVMF_VARS.fd"),
        ("edk2-ovmf/OVMF_CODE.fd", "edk2-ovmf/OVMF_VARS.fd"),
        ("qemu/ovmf-x86_64-4m-code.bin", "qemu/ovmf-x86_64-4m-vars.bin"),
        ("qemu/edk2-x86_64-code.fd", "qemu/edk2-x86_64-code.fd"),
        ("edk2-ovmf/x64/OVMF_CODE.fd", "edk2-ovmf/x64/OVMF_VARS.fd"),
        ("edk2/x64/OVMF_CODE.4m.fd", "edk2/x64/OVMF_VARS.4m.fd"),
        ("edk2/ovmf/OVMF_CODE_4M.qcow2", "edk2/ovmf/OVMF_VARS_4M.qcow2"),
    ];

    /// <summary>Every candidate path, for the probe to test (it records the ones that exist).</summary>
    public static IEnumerable<string> Candidates(string share = "/usr/share") =>
        SecureBootPairs.Concat(PlainPairs).SelectMany(p => new[] { p.Code, p.Vars }).Distinct().Select(p => Path.Combine(share, p));

    /// <summary>The firmware for a machine, or <c>null</c> when the host has none of the kind asked for.</summary>
    public static FirmwarePair? Find(IReadOnlySet<string> present, bool secureBoot, string share = "/usr/share")
    {
        FirmwarePair? found = null;
        foreach (var (code, vars) in secureBoot ? SecureBootPairs : PlainPairs)
        {
            var c = Path.Combine(share, code);
            if (present.Contains(c)) found = new FirmwarePair(c, Path.Combine(share, vars), secureBoot);
        }

        return found;
    }

    private static ReadOnlySpan<byte> QcowMagic => [0x51, 0x46, 0x49, 0xFB];

    /// <summary>quickemu's test: a qcow2 firmware file starts with "QFI\xfb"; anything else is raw.</summary>
    public static string FormatOf(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && magic.SequenceEqual(QcowMagic) ? "qcow2" : "raw";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "raw";
        }
    }
}
