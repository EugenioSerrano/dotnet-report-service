using System.Globalization;

namespace MarkdownSpike;

/// <summary>Parses a command followed by "--name value" options and "--flag" switches.</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> options = new(StringComparer.Ordinal);

    private CommandLine(string command) => Command = command;

    public string Command { get; }

    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new UsageException("A command is required.");
        }

        var line = new CommandLine(args[0]);
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                throw new UsageException($"Unexpected argument '{arg}'.");
            }

            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            line.options[arg[2..]] = value;
        }

        return line;
    }

    public void Allow(params string[] known)
    {
        foreach (string name in options.Keys)
        {
            if (Array.IndexOf(known, name) < 0)
            {
                throw new UsageException($"Unknown option --{name} for '{Command}'.");
            }
        }
    }

    public string Required(string name) =>
        Optional(name) ?? throw new UsageException($"Option --{name} is required.");

    public string? Optional(string name)
    {
        if (!options.TryGetValue(name, out string? value))
        {
            return null;
        }

        return value ?? throw new UsageException($"Option --{name} needs a value.");
    }

    public bool Flag(string name)
    {
        if (!options.TryGetValue(name, out string? value))
        {
            return false;
        }

        return value is null ? true : throw new UsageException($"Option --{name} takes no value.");
    }

    public int Int(string name, int defaultValue)
    {
        string? text = Optional(name);
        if (text is null)
        {
            return defaultValue;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : throw new UsageException($"Option --{name} needs a positive whole number.");
    }
}

/// <summary>Invalid command-line arguments; the usage text is printed and the exit code is 2.</summary>
internal sealed class UsageException(string message) : Exception(message);
