namespace VmQontrol.Core.Install;

/// <summary>
/// The keys Windows Setup needs to install a chosen edition without stopping to ask: Microsoft's published KMS client
/// setup keys ("KMS client activation and product keys", learn.microsoft.com), shared by Windows 10 and 11. They only
/// select the edition; Windows still asks to be activated with the person's own licence. Home editions have no published
/// setup key, so Easy Install asks for the person's key for those.
/// </summary>
public static class WindowsSetupKeys
{
    private static readonly (string Edition, string Key)[] Keys =
    [
        ("Pro for Workstations N", "9FNHH-K3HBT-3W4TD-6383H-6XYWF"),
        ("Pro for Workstations", "NRG8B-VKK3Q-CXVCJ-9G2XF-6Q84J"),
        ("Pro Education N", "YVWGF-BXNMC-HTQYQ-CPQ99-66QFC"),
        ("Pro Education", "6TP4R-GNPTD-KYYHQ-7B7DP-J447Y"),
        ("Pro N", "MH37W-N47XK-V7XM9-C7227-GCQG9"),
        ("Pro", "W269N-WFGWX-YVC9B-37TCY-YTCJR"),
        ("Education N", "2WH4N-8QGBV-H22JP-CT43Q-MDWWJ"),
        ("Education", "NW6C2-QMPVW-D7KKK-3GKT6-VCFB2"),
        ("Enterprise N", "DPH2V-TTNVB-4X9Q3-TJR4H-KHJW4"),
        ("Enterprise", "NPPR9-FWDCX-D2C8J-H872K-2YT43"),
    ];

    /// <summary>
    /// Keys for consumer discs (Home, Pro… on one disc): Setup on those discs refuses KMS client keys. Each one listed
    /// here was proven against a real disc by tools/probe-windows-keys.sh; an edition left out falls back to asking for
    /// the person's key.
    /// </summary>
    private static readonly (string Edition, string Key)[] Consumer =
    [
        // Proven 2026-11-02 on Windows 10 22H2 (Win10_22H2_English_x64v1.iso): Setup went on to "Installing Windows".
        // Windows 11 uses the same edition keys. Plain Home is left out: its candidate was refused.
        ("Pro N for Workstations", "WYPNQ-8C467-V2W6J-TX4WX-WT2RQ"),
        ("Pro for Workstations N", "WYPNQ-8C467-V2W6J-TX4WX-WT2RQ"),
        ("Pro for Workstations", "DXG7C-N36C4-C4HTG-X4T3X-2YV77"),
        ("Pro Education N", "GJTYN-HDMQY-FRR76-HVGC7-QPF8P"),
        ("Pro Education", "8PTT6-RNW4C-6V7J2-C2D3X-MHBPB"),
        ("Pro N", "2B87N-8KFHP-DKV6R-Y2C8J-PKCKT"),
        ("Pro", "VK7JG-NPHTM-C97JM-9MPGT-3V66T"),
        ("Education N", "84NGF-MHBT6-FXBX8-QWJK7-DRR8H"),
        ("Education", "YNMGQ-8RYV3-4PGQ3-C8XTP-7CFBY"),
        ("Home Single Language", "BT79Q-G7N6G-PGBYW-4YWX6-6F4BT"),
        ("Home N", "4CPRK-NM3K3-X6XXQ-RXX86-WXCHW"),
        // Proven 2026-11-02 on Windows 11 build 26300 (all eleven editions went on to "Installing Windows 11"). The same
        // Home key that Windows 10 22H2 refused is accepted there, so it is listed for Windows 11 only.
        ("11 Home", "YTMG3-N6DKC-DKB77-7M9GH-8HVX7"),
    ];

    /// <summary>The setup key for an edition name such as "Windows 11 Pro" on a consumer or a volume-licence disc, or <c>null</c>.</summary>
    public static string? For(string editionName, bool consumerDisc)
    {
        if (!consumerDisc) return For(editionName);
        var name = editionName.Replace("Pro N for Workstations", "Pro for Workstations N", StringComparison.OrdinalIgnoreCase).Trim();
        foreach (var (edition, key) in Consumer)
            if (name.EndsWith(" " + edition, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }

    /// <summary>The KMS client setup key for an edition name such as "Windows 11 Pro" (volume-licence discs), or <c>null</c>.</summary>
    public static string? For(string editionName)
    {
        // "Windows 10 Pro N for Workstations" names the same edition as "Pro for Workstations N".
        var name = editionName.Replace("Pro N for Workstations", "Pro for Workstations N", StringComparison.OrdinalIgnoreCase).Trim();
        foreach (var (edition, key) in Keys)
            if (name.EndsWith(" " + edition, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }
}
