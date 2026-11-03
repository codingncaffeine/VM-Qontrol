using System.Text;

namespace VmQontrol.Core.Import;

/// <summary>
/// A quickemu <c>.conf</c>, read as data: the file is a Bash script of assignments (<c>key="value"</c>,
/// <c>key=('a' 'b')</c>), and it is never run. Anything that is not a plain assignment is listed in
/// <see cref="Unread"/> so the import can say what it left behind.
/// </summary>
public sealed class QuickemuConf
{
    private QuickemuConf(Dictionary<string, string> values, Dictionary<string, List<string>> arrays, List<string> unread)
    {
        Values = values;
        Arrays = arrays;
        Unread = unread;
    }

    public IReadOnlyDictionary<string, string> Values { get; }

    public IReadOnlyDictionary<string, List<string>> Arrays { get; }

    public IReadOnlyList<string> Unread { get; }

    public string? this[string key] => Values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public IReadOnlyList<string> Array(string key) => Arrays.TryGetValue(key, out var a) ? a : [];

    public static QuickemuConf Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var arrays = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unread = new List<string>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0 || !IsName(line.AsSpan(0, eq)))
            {
                unread.Add(line);
                continue;
            }

            var key = line[..eq];
            var rest = line[(eq + 1)..];
            if (rest.StartsWith('('))
            {
                // An array may run over several lines until its closing parenthesis.
                // Only each new line is scanned (the quote state carries over), so an array left open costs one pass.
                var body = new StringBuilder(rest[1..]);
                var quote = '\0';
                var closed = EndsArray(rest.AsSpan(1), ref quote);
                while (!closed && i + 1 < lines.Length)
                {
                    var next = lines[++i].Trim();
                    body.Append(' ').Append(next);
                    closed = EndsArray(next, ref quote);
                }

                var inner = body.ToString();
                var close = inner.LastIndexOf(')');
                arrays[key] = Words(close >= 0 ? inner[..close] : inner);
            }
            else
            {
                var words = Words(rest);
                values[key] = words.Count > 0 ? words[0] : "";
            }
        }

        return new QuickemuConf(values, arrays, unread);
    }

    public static QuickemuConf Load(string file) => Parse(File.ReadAllText(file));

    private static bool IsName(ReadOnlySpan<char> s)
    {
        if (s.Length == 0 || !(char.IsAsciiLetter(s[0]) || s[0] == '_')) return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    /// <summary>Whether this piece closes the array; <paramref name="quote"/> carries an open quote between pieces.</summary>
    private static bool EndsArray(ReadOnlySpan<char> s, ref char quote)
    {
        foreach (var c in s)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == ')') return true;
        }

        return false;
    }

    /// <summary>Shell words: quotes group and are removed, a backslash escapes, an unquoted # starts a comment.</summary>
    private static List<string> Words(string s)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        var quote = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && quote == '"' && i + 1 < s.Length) current.Append(s[++i]);
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'') { quote = c; inWord = true; }
            else if (c == '\\' && i + 1 < s.Length) { current.Append(s[++i]); inWord = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord) { words.Add(current.ToString()); current.Clear(); inWord = false; }
            }
            else if (c == '#' && !inWord) break;
            else { current.Append(c); inWord = true; }
        }

        if (inWord) words.Add(current.ToString());
        return words;
    }
}
