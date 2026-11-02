using System.Text.Json;

namespace VmQontrol.Core.Machines;

/// <summary>Reads and writes <c>vm.json</c>. A write goes to a new file that replaces the old one only once complete.</summary>
public static class MachineStore
{
    public const string FileName = "vm.json";

    public static MachineConfig Load(string file)
    {
        var text = File.ReadAllText(file);
        var config = JsonSerializer.Deserialize(text, MachineJsonContext.Default.MachineConfig)
                     ?? throw new InvalidDataException($"{file} is empty.");
        // A file written by hand (or by an old build) without an id would get a new random one at every read, and its
        // running machine could never be found again: it gets one from where it is instead, the same every time.
        if (System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject json && json["id"] is null)
            config.Id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(file))))[..32];
        if (config.SchemaVersion > MachineConfig.CurrentSchema)
            throw new InvalidDataException($"{file} was written by a newer VM Qontrol (format {config.SchemaVersion}); update VM Qontrol to open it.");
        return config;
    }

    public static void Save(MachineConfig config, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
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
