namespace VmQontrol.Core.Qemu;

/// <summary>
/// quickemu's add_cpu_flag: appends a flag to the -cpu value unless it repeats one already added, contradicts one
/// (+x after -x), or sets a value already set. Flags in the base model string are not tracked, as in quickemu.
/// </summary>
internal sealed class CpuFlagList(string baseValue)
{
    private readonly HashSet<string> _added = new(StringComparer.Ordinal);
    private string _value = baseValue;

    public void Add(string flag)
    {
        flag = flag.TrimStart(',');
        if (flag.Length == 0 || !_added.Add(flag)) return;
        var prefix = flag[0] is '+' or '-' ? flag[0].ToString() : "";
        var name = (prefix.Length > 0 ? flag[1..] : flag).Split('=')[0];
        var opposite = prefix switch { "+" => "-" + name, "-" => "+" + name, _ => null };
        if ((opposite is not null && _added.Contains(opposite))
            || (flag.Contains('=') && _added.Any(e => e != flag && e.Contains('=') && e.Split('=')[0] == name)))
        {
            _added.Remove(flag);
            return;
        }

        _value += "," + flag;
    }

    public override string ToString() => _value;
}
