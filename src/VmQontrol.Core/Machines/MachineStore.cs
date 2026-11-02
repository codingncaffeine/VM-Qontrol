using System.Text.Json;

namespace VmQontrol.Core.Machines;

/// <summary>Reads and writes <c>vm.json</c>. A write goes to a new file that replaces the old one only once complete.</summary>
public static class MachineStore
{
    public const string FileName = "vm.json";

    public static MachineConfig Load(string file)
    {
        using var stream = File.OpenRead(file);
        var config = JsonSerializer.Deserialize(stream, MachineJsonContext.Default.MachineConfig)
                     ?? throw new InvalidDataException($"{file} is empty.");
        if (config.SchemaVersion > MachineConfig.CurrentSchema)
            throw new InvalidDataException($"{file} was written by a newer VM Qontrol (format {config.SchemaVersion}); update VM Qontrol to open it.");
        return config;
    }

    public static void Save(MachineConfig config, string file)
    {
        var temp = file + ".new";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, config, MachineJsonContext.Default.MachineConfig);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, file, overwrite: true);
    }

    public static string ToJson(MachineConfig config) => JsonSerializer.Serialize(config, MachineJsonContext.Default.MachineConfig);
}
