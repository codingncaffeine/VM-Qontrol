using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Install;

/// <summary>Where an Easy Install stands, as the machine page shows it.</summary>
public enum InstallStage
{
    /// <summary>The machine was not made with Easy Install (or was imported): nothing to show.</summary>
    None,

    /// <summary>Made with Easy Install and never powered on.</summary>
    NotStarted,

    /// <summary>Powered on at least once; the guest has not said it finished.</summary>
    Installing,

    /// <summary>The guest wrote <see cref="InstallSignal.Marker"/>: Windows reached its first sign-in, Linux finished installing.</summary>
    Finished,
}

/// <summary>An Easy Install's stage, with when it started and finished when known.</summary>
/// <param name="Stage">Where it stands.</param>
/// <param name="Started">The first power on.</param>
/// <param name="Finished">When the guest's marker arrived (or was first seen).</param>
/// <param name="Acknowledged">The person closed the "installed" card (a "seen" line in the signal file).</param>
public sealed record InstallProgress(InstallStage Stage, DateTimeOffset? Started = null, DateTimeOffset? Finished = null, bool Acknowledged = false)
{
    /// <summary>How long the install took, when both ends are known.</summary>
    public TimeSpan? Took => Started is { } s && Finished is { } f && f > s ? f - s : null;
}

/// <summary>
/// How a machine tells VM Qontrol that Easy Install finished: while it installs, the machine has a fourth COM port
/// (0x2e8: ttyS3 on Linux, ACPI _UID 4 on Windows) whose output QEMU appends to <see cref="FileName"/> in the machine's
/// folder. The answer file's last step writes <see cref="Marker"/> to it. The disk's size cannot say this: it counts as
/// "used" seconds into Setup.
/// </summary>
public static class InstallSignal
{
    public const string FileName = "install-signal.log";

    public const string Marker = "VMQONTROL-INSTALLED";

    /// <summary>ISA serial index 3 = the fourth COM port.</summary>
    public const int PortIndex = 3;

    private const string StartedPrefix = "started ";

    private const string SeenLine = "seen";

    /// <summary>Windows first-logon step: write the marker to the port whose ACPI instance is _UID 4 (or the only port there is).</summary>
    public const string WindowsCommand =
        "powershell -NoProfile -ExecutionPolicy Bypass -Command \"$ports = @(Get-CimInstance Win32_SerialPort); " +
        "$p = $ports | Where-Object { $_.PNPDeviceID -like 'ACPI\\PNP0501\\4' } | Select-Object -First 1; " +
        "if (-not $p -and $ports.Count -eq 1) { $p = $ports[0] }; " +
        "if ($p) { $s = New-Object System.IO.Ports.SerialPort $p.DeviceID; $s.Open(); $s.WriteLine('" + Marker + "'); $s.Close() }\"";

    /// <summary>Linux installers' last step (run in the installer, before it restarts into the new system).</summary>
    public const string LinuxCommand = "echo " + Marker + " > /dev/ttyS3";

    public static InstallProgress Read(MachineConfig m, string machineDirectory)
    {
        if (m.EasyInstall is null) return new(InstallStage.None);
        var file = Path.Combine(machineDirectory, FileName);
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(InstallStage.NotStarted);
        }

        DateTimeOffset? started = null;
        if (text.StartsWith(StartedPrefix, StringComparison.Ordinal)
            && DateTimeOffset.TryParse(text.AsSpan(StartedPrefix.Length, text.IndexOf('\n') is var nl and > 0 ? nl - StartedPrefix.Length : text.Length - StartedPrefix.Length),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
            started = at;
        if (!text.Contains(Marker, StringComparison.Ordinal)) return new(InstallStage.Installing, started);
        // The guest's marker is the last thing QEMU wrote, unless "seen" was added after it.
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var seen = lines.Contains(SeenLine, StringComparer.Ordinal);
        DateTimeOffset? finished = null;
        foreach (var line in lines)
            if (line.StartsWith("finished ", StringComparison.Ordinal) && DateTimeOffset.TryParse(line.AsSpan(9), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var f))
                finished = f;
        finished ??= new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        return new(InstallStage.Finished, started, finished, seen);
    }

    /// <summary>
    /// Records when the finish was first seen (the file's time changes once "seen" is added, so the finish time is kept
    /// in a line of its own the first time the app reads the marker).
    /// </summary>
    public static void NoteFinished(string machineDirectory, DateTimeOffset at)
    {
        var file = Path.Combine(machineDirectory, FileName);
        if (!File.ReadAllText(file).Contains("\nfinished ", StringComparison.Ordinal))
            File.AppendAllText(file, "finished " + at.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n");
    }

    /// <summary>The person closed the "installed" card: it stays closed.</summary>
    public static void Acknowledge(string machineDirectory, DateTimeOffset finished)
    {
        NoteFinished(machineDirectory, finished);
        File.AppendAllText(Path.Combine(machineDirectory, FileName), SeenLine + "\n");
    }

    /// <summary>
    /// At power on: the signal file for QEMU's port, made with the start time on its first line the first time, or
    /// <c>null</c> when the machine needs no port (no Easy Install, or already finished).
    /// </summary>
    public static string? Prepare(MachineConfig m, string machineDirectory)
    {
        var stage = Read(m, machineDirectory).Stage;
        if (stage is InstallStage.None or InstallStage.Finished) return null;
        var file = Path.Combine(machineDirectory, FileName);
        if (stage == InstallStage.NotStarted)
            File.WriteAllText(file, StartedPrefix + DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        return file;
    }
}
