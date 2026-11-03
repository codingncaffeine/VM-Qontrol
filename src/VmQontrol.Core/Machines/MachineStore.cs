using System.Text.Json;

namespace VmQontrol.Core.Machines;

/// <summary>Reads and writes <c>vm.json</c>. A write goes to a new file that replaces the old one only once complete.</summary>
public static partial class MachineStore
{
    public const string FileName = "vm.json";

    public static MachineConfig Load(string file)
    {
        var text = File.ReadAllText(file);
        var config = JsonSerializer.Deserialize(text, MachineJsonContext.Default.MachineConfig)
                     ?? throw new InvalidDataException($"{file} is empty.");
        // A file written by hand (or by an old build) without an id would get a new random one at every read, and its
        // running machine could never be found again: it gets one from where it is instead, the same every time.
        // An id that is not one (empty, a path, "../x") would name folders outside VM Qontrol's own: it is replaced the same way.
        if (System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject json && json["id"] is null
            || !IdPattern().IsMatch(config.Id ?? ""))
            config.Id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(file))))[..32];
        if (MissingRequired(config) is { } missing)
            throw new InvalidDataException($"{file} has no value for \"{missing}\" (it holds null). Fix or remove that line.");
        if (config.SchemaVersion > MachineConfig.CurrentSchema)
            throw new InvalidDataException($"{file} was written by a newer VM Qontrol (format {config.SchemaVersion}); update VM Qontrol to open it.");
        return config;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[0-9A-Za-z-]{8,64}$")]
    private static partial System.Text.RegularExpressions.Regex IdPattern();

    private static readonly System.Reflection.NullabilityInfoContext Nullability = new();

    /// <summary>
    /// The first setting that may not be null but is (a hand-edited or damaged file: JSON's null passes the reader for
    /// a list or a section), looked for in the machine and in each of its sections; <c>null</c> = none.
    /// </summary>
    private static string? MissingRequired(object config, string prefix = "", int depth = 0)
    {
        foreach (var p in config.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (p.PropertyType.IsValueType || p.GetIndexParameters().Length > 0 || !p.CanWrite) continue;
            var value = p.GetValue(config);
            if (value is null)
            {
                if (Nullability.Create(p).ReadState == System.Reflection.NullabilityState.NotNull) return prefix + p.Name;
                continue;
            }

            if (depth < 2 && p.PropertyType.Name.EndsWith("Config", StringComparison.Ordinal) && MissingRequired(value, prefix + p.Name + ".", depth + 1) is { } inner) return inner;
            if (value is System.Collections.IList list && list.Cast<object?>().Any(x => x is null)) return prefix + p.Name + "[]";
        }

        return null;
    }

    public static void Save(MachineConfig config, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temp = $"{file}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.new";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, config, MachineJsonContext.Default.MachineConfig);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, file, overwrite: true);
    }

    public static string ToJson(MachineConfig config) => JsonSerializer.Serialize(config, MachineJsonContext.Default.MachineConfig);
}
