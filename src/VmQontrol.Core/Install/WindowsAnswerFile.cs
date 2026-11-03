using System.Xml.Linq;

namespace VmQontrol.Core.Install;

/// <summary>What the person chose for an unattended Windows installation.</summary>
public sealed record WindowsInstallChoices
{
    public required string UserName { get; init; }

    /// <summary>May be empty: Windows allows an account without a password.</summary>
    public string Password { get; init; } = "";

    public required string ComputerName { get; init; }

    /// <summary>The edition's index in the disc's install image.</summary>
    public int EditionIndex { get; init; } = 1;

    /// <summary>The edition's name ("Windows 11 Pro"), which picks Microsoft's setup key when no key is given.</summary>
    public string EditionName { get; init; } = "";

    /// <summary>A consumer disc (Home, Pro… together) rather than a volume-license disc: they take different setup keys.</summary>
    public bool ConsumerDisc { get; init; } = true;

    /// <summary>A product key, or empty to use the edition's published setup key (Windows asks for activation later).</summary>
    public string ProductKey { get; init; } = "";

    /// <summary>Language and region, e.g. "en-US".</summary>
    public string Locale { get; init; } = "en-US";

    /// <summary>The keyboard, e.g. "0409:00000409" (US); empty = the locale's default.</summary>
    public string InputLocale { get; init; } = "";

    /// <summary>The disc's own language for Setup's screens and Windows' display language ("en-US"); empty = <see cref="Locale"/>.
    /// A downloaded disc carries one language, which can differ from the person's region.</summary>
    public string UiLanguage { get; init; } = "";

    /// <summary>A Windows time zone name, e.g. "Pacific Standard Time".</summary>
    public string TimeZone { get; init; } = "UTC";

    public bool AutoLogon { get; init; } = true;

    public bool Uefi { get; init; } = true;

    /// <summary>The VirtIO driver disc is attached: load its disk and network drivers, and install its guest tools at first logon.</summary>
    public bool VirtioDrivers { get; init; } = true;

    /// <summary>Windows 11 on a machine without a TPM or Secure Boot: tell Setup not to check for them.</summary>
    public bool BypassWindows11Checks { get; init; }

    /// <summary>"Lean Windows" (<see cref="WindowsLean"/>): the answer disc carries the script and the specialize pass runs it.</summary>
    public bool Lean { get; init; }

    /// <summary>Commands to run at the end of the first logon (tests use one to power the machine off).</summary>
    public IReadOnlyList<string> FirstLogonCommands { get; init; } = [];
}

/// <summary>
/// Writes autounattend.xml for Windows 10 and 11 from Microsoft's documented unattend schema
/// (urn:schemas-microsoft-com:unattend): disk layout for UEFI or BIOS, the chosen edition, a local account, the VirtIO
/// drivers from the driver disc, and an out-of-box experience with no questions.
/// </summary>
public static class WindowsAnswerFile
{
    private static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    /// <summary>The virtio-win driver folders Setup needs (disk, network, and the rest for a complete system).</summary>
    private static readonly string[] DriverFolders = ["viostor", "vioscsi", "NetKVM", "Balloon", "vioserial", "viorng", "qxldod", "vioinput", "pvpanic"];

    public static string Build(WindowsInstallChoices c, string windowsRelease)
    {
        var driverOs = windowsRelease == "11" ? "w11" : "w10";
        var locale = c.Locale;
        var input = c.InputLocale.Length > 0 ? c.InputLocale : locale;
        var ui = c.UiLanguage.Length > 0 ? c.UiLanguage : locale;

        XElement Component(string name, params object[] content) => new(U + "component",
            new XAttribute("name", name), new XAttribute("processorArchitecture", "amd64"),
            new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"),
            content);
        XElement E(string name, params object[] content) => new(U + name, content);
        XElement Added(string name, int order, params object[] content) => new(U + name, new XAttribute(Wcm + "action", "add"), E("Order", order), content);

        var partitions = c.Uefi
            ? new[]
            {
                Added("CreatePartition", 1, E("Type", "EFI"), E("Size", 300)),
                Added("CreatePartition", 2, E("Type", "MSR"), E("Size", 16)),
                Added("CreatePartition", 3, E("Type", "Primary"), E("Extend", "true")),
            }
            : [Added("CreatePartition", 1, E("Type", "Primary"), E("Extend", "true"))];
        var formats = c.Uefi
            ? new[]
            {
                Added("ModifyPartition", 1, E("PartitionID", 1), E("Format", "FAT32"), E("Label", "System")),
                Added("ModifyPartition", 2, E("PartitionID", 3), E("Format", "NTFS"), E("Label", "Windows"), E("Letter", "C")),
            }
            : [Added("ModifyPartition", 1, E("PartitionID", 1), E("Format", "NTFS"), E("Label", "Windows"), E("Letter", "C"), E("Active", "true"))];

        var setup = Component("Microsoft-Windows-Setup",
            E("DiskConfiguration", E("Disk", new XAttribute(Wcm + "action", "add"), E("DiskID", 0), E("WillWipeDisk", "true"),
                E("CreatePartitions", partitions), E("ModifyPartitions", formats))),
            E("ImageInstall", E("OSImage",
                E("InstallFrom", E("MetaData", new XAttribute(Wcm + "action", "add"), E("Key", "/IMAGE/INDEX"), E("Value", c.EditionIndex))),
                E("InstallTo", E("DiskID", 0), E("PartitionID", c.Uefi ? 3 : 1)))),
            E("UserData", E("AcceptEula", "true"),
                E("ProductKey", E("Key", KeyFor(c)), E("WillShowUI", "OnError"))));
        if (c.BypassWindows11Checks)
        {
            // Microsoft's documented LabConfig switches, set in Windows PE before Setup checks the hardware.
            var order = 1;
            setup.Add(E("RunSynchronous", new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck" }.Select(v =>
                Added("RunSynchronousCommand", order++, E("Path", $@"reg add HKLM\SYSTEM\Setup\LabConfig /v {v} /t REG_DWORD /d 1 /f")))));
        }

        var windowsPe = new List<object>
        {
            Component("Microsoft-Windows-International-Core-WinPE", E("SetupUILanguage", E("UILanguage", ui)),
                E("InputLocale", input), E("SystemLocale", locale), E("UILanguage", ui), E("UserLocale", locale)),
            setup,
        };
        if (c.VirtioDrivers)
        {
            // The driver disc's drive letter depends on how many discs and disks there are; Setup ignores paths that do not exist.
            var key = 1;
            windowsPe.Add(Component("Microsoft-Windows-PnpCustomizationsWinPE", E("DriverPaths",
                from letter in "DEFGH"
                from folder in DriverFolders
                select new XElement(U + "PathAndCredentials", new XAttribute(Wcm + "action", "add"), new XAttribute(Wcm + "keyValue", (key++).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    E("Path", $@"{letter}:\{folder}\{driverOs}\amd64")))));
        }

        var account = E("LocalAccount", new XAttribute(Wcm + "action", "add"),
            E("Name", c.UserName), E("DisplayName", c.UserName), E("Group", "Administrators"),
            E("Password", E("Value", c.Password), E("PlainText", "true")));
        var firstLogon = new List<string>();
        if (c.VirtioDrivers)
        {
            // The driver disc's installer adds the guest agent (address, clean shutdown) and the SPICE agent (clipboard, resizing).
            firstLogon.Add(@"cmd /c for %d in (D E F G H) do if exist %d:\virtio-win-guest-tools.exe start /wait %d:\virtio-win-guest-tools.exe /install /passive /norestart");
        }

        // Tells VM Qontrol the install reached the desktop (InstallSignal): the machine page stops saying "installing".
        firstLogon.Add(InstallSignal.WindowsCommand);
        firstLogon.AddRange(c.FirstLogonCommands);
        var shellOobe = new List<object>
        {
            E("OOBE", E("HideEULAPage", "true"), E("HideOEMRegistrationScreen", "true"), E("HideOnlineAccountScreens", "true"),
                E("HideWirelessSetupInOOBE", "true"), E("ProtectYourPC", 3), E("SkipMachineOOBE", "true"), E("SkipUserOOBE", "true")),
            E("UserAccounts", E("LocalAccounts", account)),
            E("TimeZone", c.TimeZone),
        };
        if (c.AutoLogon)
            shellOobe.Add(E("AutoLogon", E("Enabled", "true"), E("LogonCount", 9999999), E("Username", c.UserName), E("Password", E("Value", c.Password), E("PlainText", "true"))));
        if (firstLogon.Count > 0)
        {
            var order = 1;
            shellOobe.Add(E("FirstLogonCommands", firstLogon.Select(cmd =>
                Added("SynchronousCommand", order++, E("CommandLine", cmd), E("Description", $"VM Qontrol step {order - 1}")))));
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(U + "unattend",
                new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName),
                E("settings", new XAttribute("pass", "windowsPE"), windowsPe),
                E("settings", new XAttribute("pass", "specialize"),
                    Component("Microsoft-Windows-Shell-Setup", E("ComputerName", c.ComputerName), E("TimeZone", c.TimeZone)),
                    // No "Do you want to allow your PC to be discoverable?" question at the first sign-in.
                    Component("Microsoft-Windows-Deployment", E("RunSynchronous",
                        Added("RunSynchronousCommand", 1, E("Path", @"reg add HKLM\SYSTEM\CurrentControlSet\Control\Network\NewNetworkWindowOff /f")),
                        c.Lean ? [Added("RunSynchronousCommand", 2, E("Path", WindowsLean.Command), E("Description", "VM Qontrol: Lean Windows"))] : Array.Empty<object>()))),
                E("settings", new XAttribute("pass", "oobeSystem"),
                    Component("Microsoft-Windows-International-Core", E("InputLocale", input), E("SystemLocale", locale), E("UILanguage", ui), E("UserLocale", locale)),
                    Component("Microsoft-Windows-Shell-Setup", shellOobe))));
        return document.Declaration + Environment.NewLine + document.ToString();
    }

    /// <summary>The person's key, else Microsoft's setup key for the edition; Setup shows its key page only when there is neither.</summary>
    public static string KeyFor(WindowsInstallChoices c) =>
        c.ProductKey.Trim() is { Length: > 0 } own ? own : WindowsSetupKeys.For(c.EditionName, c.ConsumerDisc) ?? "";

    /// <summary>Whether Easy Install can finish without stopping: a key of the person's own, or a published setup key.</summary>
    public static bool HasKey(WindowsInstallChoices c) => KeyFor(c).Length > 0;

    /// <summary>A Windows computer name: up to 15 letters, digits and hyphens, not all digits.</summary>
    public static string ComputerName(string machineName)
    {
        var chars = machineName.ToUpperInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(15).ToArray();
        var name = new string(chars).Trim('-');
        return name.Length == 0 || name.All(char.IsAsciiDigit) ? "VM-" + name : name;
    }
}
