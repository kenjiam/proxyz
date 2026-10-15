namespace Proxyz.Cli;

public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    public List<string> Words { get; } = [];
    public bool Has(string option) => _options.ContainsKey(option);
    public string? Value(string option) => _options.GetValueOrDefault(option);
    public string Required(string option) => Value(option) ?? throw new ArgumentException($"--{option} requires a value.");
    public bool Boolean(string option) => ParseBoolean(Required(option));
    public static bool ParseBoolean(string value) => value switch
    {
        "true" => true, "false" => false, _ => throw new ArgumentException("Expected true or false.")
    };
    public void Allow(params string[] options)
    {
        var unknown = _options.Keys.Except(options).FirstOrDefault();
        if (unknown is not null) throw new ArgumentException($"Unknown option: --{unknown}");
    }
    public void Count(int count)
    {
        if (Words.Count != count) throw new ArgumentException("Incorrect arguments. See proxyz --help.");
    }
    public static CommandLine Parse(IEnumerable<string> args)
    {
        var result = new CommandLine();
        var values = new HashSet<string>(["server", "pac", "bypass", "flags", "direct-flag", "wpad", "manual", "script", "note", "name", "profile", "network-id", "ssid", "ssid-hex", "kind", "url"], StringComparer.Ordinal);
        var list = args.ToArray();
        for (int i = 0; i < list.Length; i++)
        {
            var arg = list[i];
            if (arg.Length > 16000) throw new ArgumentException("Argument is too long.");
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { result.Words.Add(arg); continue; }
            var pair = arg[2..].Split('=', 2);
            if (pair[0].Length == 0 || result.Has(pair[0])) throw new ArgumentException("Empty or repeated option.");
            string? value = pair.Length == 2 ? pair[1] : null;
            if (pair.Length == 1 && values.Contains(pair[0]))
            {
                if (++i >= list.Length || list[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"--{pair[0]} requires a value.");
                value = list[i];
            }
            if (!values.Contains(pair[0]) && value is not null) throw new ArgumentException($"--{pair[0]} does not accept a value.");
            result._options.Add(pair[0], value);
        }
        return result;
    }
}
