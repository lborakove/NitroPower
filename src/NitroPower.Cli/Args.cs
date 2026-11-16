using System.Globalization;

namespace NitroPower.Cli;

/// <summary>Bad command line; the message is shown with a pointer to --help.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Positional words plus "--name value" options and "--flag" switches.</summary>
internal sealed class Args
{
    private static readonly HashSet<string> Switches = ["--notify", "--direct", "--help", "-h"];

    private readonly List<string> _words = [];
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private int _next;

    public Args(IReadOnlyList<string> argv)
    {
        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            if (Switches.Contains(a))
                _flags.Add(a);
            else if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                if (i + 1 >= argv.Count)
                    throw new UsageException($"{a} needs a value");
                _options[a] = argv[++i];
            }
            else
                _words.Add(a);
        }
    }

    public bool HasFlag(string flag) => _flags.Contains(flag);

    public string? Option(string name)
    {
        _usedOptions.Add(name);
        return _options.GetValueOrDefault(name);
    }

    private readonly HashSet<string> _usedOptions = new(StringComparer.Ordinal);

    public int? IntOption(string name, int min, int max)
    {
        if (Option(name) is not { } text)
            return null;
        return ParseInt(text, name, min, max);
    }

    /// <summary>Next positional word, or null when there are no more.</summary>
    public string? Next() => _next < _words.Count ? _words[_next++] : null;

    public string Require(string what) => Next() ?? throw new UsageException($"missing {what}");

    public IReadOnlyList<string> Rest()
    {
        var rest = _words.Skip(_next).ToList();
        _next = _words.Count;
        return rest;
    }

    /// <summary>Rejects leftover words and options nobody asked for.</summary>
    public void End()
    {
        if (_next < _words.Count)
            throw new UsageException($"unexpected '{_words[_next]}'");
        foreach (var name in _options.Keys)
        {
            if (!_usedOptions.Contains(name))
                throw new UsageException($"unknown option {name}");
        }
    }

    public static int ParseInt(string text, string what, int min, int max) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? v
            : throw new UsageException($"{what} must be a number from {min} to {max}");

    public static bool ParseOnOff(string text) => text.ToLowerInvariant() switch
    {
        "on" or "1" or "true" or "yes" or "enable" or "start" => true,
        "off" or "0" or "false" or "no" or "disable" or "stop" => false,
        _ => throw new UsageException($"expected on or off, got '{text}'"),
    };
}
