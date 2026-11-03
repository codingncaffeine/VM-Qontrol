using System.Diagnostics;

namespace VmQontrol.Core.Hosts;

/// <summary>
/// Nested virtualization (Workstation's "Virtualize Intel VT-x/EPT or AMD-V/RVI"): whether this computer's KVM lets a
/// guest run its own virtual machines, and turning that on (one polkit prompt writes a modprobe option and reloads the
/// module when nothing holds it).
/// </summary>
public static class NestedVirt
{
    /// <summary>The KVM module of this vendor ("kvm_amd" or "kvm_intel"), or <c>null</c> for others.</summary>
    public static string? Module(HostCapabilities host) => host.IsAmd ? "kvm_amd" : host.IsIntel ? "kvm_intel" : null;

    /// <summary>The CPU flag the guest needs ("svm" or "vmx").</summary>
    public static string Flag(HostCapabilities host) => host.IsAmd ? "svm" : "vmx";

    /// <summary>Reads the module's nested parameter: "Y"/"1" = on; <c>null</c> = the module is not loaded.</summary>
    public static bool? Read(string? module, string sysRoot = "/sys/module")
    {
        if (module is null) return null;
        var file = Path.Combine(sysRoot, module, "parameters", "nested");
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() is "Y" or "y" or "1" : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The script the polkit prompt runs: the option for every boot, then a reload if no machine holds the module.</summary>
    public static string Script(string module) =>
        $"printf 'options {module} nested=1\\n' > /etc/modprobe.d/vmqontrol-nested.conf && " +
        $"(modprobe -r {module} 2>/dev/null && modprobe {module}; true)";

    /// <summary>Turns it on (the password is asked for); returns what happened in words, and whether it is on now.</summary>
    public static async Task<(bool OnNow, string Message)> EnableAsync(HostCapabilities host, CancellationToken ct = default)
    {
        if (Module(host) is not { } module) return (false, "This processor's KVM module is not one VM Qontrol knows how to set.");
        if (!File.Exists("/usr/bin/pkexec")) return (false, "polkit's pkexec is not installed, so the password cannot be asked for.");
        var start = new ProcessStartInfo("/usr/bin/pkexec") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in (string[])["/bin/sh", "-c", Script(module)]) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode is 126 or 127) return (false, p.ExitCode == 126 ? "The password prompt was closed." : "polkit refused.");
        if (p.ExitCode != 0) return (false, error.Trim());
        return Read(module) == true
            ? (true, "Nested virtualization is on for this computer.")
            : (false, "Turned on for the next start of this computer (running machines hold KVM now, so it could not be reloaded).");
    }
}
