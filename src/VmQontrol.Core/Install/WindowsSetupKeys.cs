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

    /// <summary>The setup key for an edition name such as "Windows 11 Pro", or <c>null</c> when none is published (Home).</summary>
    public static string? For(string editionName)
    {
        // "Windows 10 Pro N for Workstations" names the same edition as "Pro for Workstations N".
        var name = editionName.Replace("Pro N for Workstations", "Pro for Workstations N", StringComparison.OrdinalIgnoreCase).Trim();
        foreach (var (edition, key) in Keys)
            if (name.EndsWith(" " + edition, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }
}
