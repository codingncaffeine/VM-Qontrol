using VmQontrol.Core.Import;
using VmQontrol.Core.Qemu;

namespace VmQontrol.Tests;

/// <summary>
/// The command builder against quickemu itself: each reference .conf was run through the real quickemu with a stand-in
/// QEMU that only recorded its arguments (tools/record-oracle.sh). The import of the same .conf, built for the same
/// host, must give the same command, argument for argument. Only the socket and process-id paths differ (VM Qontrol
/// keeps them in a short runtime folder) and the monitor (VM Qontrol uses QMP); both sides are normalised for those.
/// </summary>
public sealed class QuickemuParityTests
{
    public static TheoryData<string> Configs() =>
        new(Directory.GetFiles(RecordedHost.FixtureDirectory, "*.conf").Select(Path.GetFileNameWithoutExtension).Order()!);

    [Theory]
    [MemberData(nameof(Configs))]
    public void The_command_matches_quickemus(string name)
    {
        var dir = RecordedHost.FixtureDirectory;
        var import = QuickemuImporter.Import(Path.Combine(dir, name + ".conf"));
        var runtime = new RuntimePaths("/run/user/1000/vmqontrol/t");
        var launch = QemuCommandBuilder.Build(import.Machine, new LaunchContext
        {
            Host = RecordedHost.Load(),
            MachineDirectory = import.MachineDirectory,
            Runtime = runtime,
            FileExists = _ => false,
            FirmwareFormat = _ => "raw",
        });

        Assert.Empty(launch.Errors);
        var ours = Normalise(launch.Args.Select(a => a.Replace(dir + "/", "", StringComparison.Ordinal).Replace(runtime.Directory, "<RUN>", StringComparison.Ordinal)).ToList(), name);
        var theirs = Normalise(File.ReadAllLines(Path.Combine(dir, name + ".args")).Select(a => a.Replace("<ROOT>/", "", StringComparison.Ordinal)).ToList(), name);
        Assert.Equal(string.Join('\n', theirs), string.Join('\n', ours));
    }

    private static List<string> Normalise(List<string> args, string name)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            // The monitor differs on purpose: quickemu opens a human monitor socket, VM Qontrol turns it off and uses QMP.
            if (a is "-monitor" or "-qmp") { i++; continue; }
            if (a == "-serial" && args[i + 1] != "none") { result.AddRange(["-serial", "<SERIAL>"]); i++; continue; }
            a = a.Replace($"{name}/{name}.pid", "<PID>", StringComparison.Ordinal).Replace("<RUN>/qemu.pid", "<PID>", StringComparison.Ordinal)
                .Replace($"{name}/{name}.sock", "<SPICE>", StringComparison.Ordinal).Replace("<RUN>/spice.sock", "<SPICE>", StringComparison.Ordinal)
                .Replace($"{name}/{name}-agent.sock", "<AGENT>", StringComparison.Ordinal).Replace("<RUN>/agent.sock", "<AGENT>", StringComparison.Ordinal)
                .Replace($"{name}/{name}.swtpm-sock", "<TPM>", StringComparison.Ordinal).Replace("<RUN>/swtpm.sock", "<TPM>", StringComparison.Ordinal);
            result.Add(a);
        }

        return result;
    }
}
