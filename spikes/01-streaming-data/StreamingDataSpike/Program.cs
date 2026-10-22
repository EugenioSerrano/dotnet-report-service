using System.Globalization;
using StreamingDataSpike.Parsing;

namespace StreamingDataSpike;

/// <summary>Entry point: dispatches to one command.</summary>
internal static class Program
{
    public const string Usage = """
        Measures the incremental envelope reader and three template engines.

        Usage:
          prepare      --datasets DIR
          hostile      --output DIR [--from FILE]
          parse        FILE [limits]
          render       FILE --template ID --engine scriban|handlebars|liquid [--context per-record|per-block] [limits]
          compare      FILE --template ID [--culture NAME] [--parallel N] [limits]
          escape-probe
          limits-probe
          bench        --datasets DIR --output DIR [--runs N]
          selfcheck    --datasets DIR [--hostile DIR] [--output DIR]

        Limits: --header-limit BYTES (1048576), --record-limit BYTES (1048576),
        --token-limit BYTES (1048576), --depth-limit N (64), --input-limit BYTES
        (0 = unlimited), --initial-buffer BYTES (4096, minimum 16),
        --block-records N (1000), --block-bytes BYTES (8388608).

        Exit codes: 0 success, 1 failure, 2 invalid arguments, 3 data error
        (one line on standard error: error code=... limit=|reason=... ordinal=... offset=...).
        """;

    public static async Task<int> Main(string[] args)
    {
        // Results are redirected to files: always UTF-8 without a byte-order mark.
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            if (args.Length == 0)
            {
                throw new UsageException("A command is required.");
            }

            var arguments = Arguments.Parse(args.AsSpan(1));
            return args[0] switch
            {
                "prepare" => await Commands.PrepareAsync(arguments).ConfigureAwait(false),
                "hostile" => await HostileInputs.WriteAsync(arguments).ConfigureAwait(false),
                "parse" => await Commands.ParseAsync(arguments).ConfigureAwait(false),
                "render" => await Commands.RenderAsync(arguments).ConfigureAwait(false),
                "compare" => await Commands.CompareAsync(arguments).ConfigureAwait(false),
                "escape-probe" => Probes.Escape(),
                "limits-probe" => Probes.Limits(),
                "bench" => await Bench.RunAsync(arguments).ConfigureAwait(false),
                "selfcheck" => await SelfCheck.RunAsync(arguments).ConfigureAwait(false),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (EnvelopeException exception)
        {
            Console.Error.WriteLine(exception.ToErrorLine());
            return 3;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("failure: " + exception.Message);
            return 1;
        }
    }
}

/// <summary>Invalid command-line arguments (exit code 2).</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Options (<c>--name value</c>) and positional arguments of one command.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public static Arguments Parse(ReadOnlySpan<string> args)
    {
        var result = new Arguments();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new UsageException($"Option {args[i]} needs a value.");
                }

                result._options[args[i]] = args[++i];
            }
            else
            {
                result._positional.Add(args[i]);
            }
        }

        return result;
    }

    public string Positional(int index, string name) =>
        index < _positional.Count ? _positional[index] : throw new UsageException($"The {name} argument is required.");

    public string? Option(string name)
    {
        _used.Add(name);
        return _options.GetValueOrDefault(name);
    }

    public string Required(string name) => Option(name) ?? throw new UsageException($"Option {name} is required.");

    public long Number(string name, long defaultValue, long minimum = 0)
    {
        var text = Option(name);
        if (text is null)
        {
            return defaultValue;
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum)
        {
            throw new UsageException($"Option {name} needs a whole number of at least {minimum}.");
        }

        return value;
    }

    /// <summary>Rejects any option that the command did not read.</summary>
    public void EnsureAllUsed(int positionalCount)
    {
        var unknown = _options.Keys.Where(key => !_used.Contains(key)).ToList();
        if (unknown.Count > 0)
        {
            throw new UsageException("Unknown option(s): " + string.Join(", ", unknown));
        }

        if (_positional.Count != positionalCount)
        {
            throw new UsageException($"Expected {positionalCount} positional argument(s), found {_positional.Count}.");
        }
    }

    /// <summary>The reader limits of B.1 from the command line.</summary>
    public EnvelopeLimits Limits()
    {
        var defaults = new EnvelopeLimits();
        return new EnvelopeLimits
        {
            HeaderLimit = Number("--header-limit", defaults.HeaderLimit, 1),
            RecordLimit = Number("--record-limit", defaults.RecordLimit, 1),
            TokenLimit = Number("--token-limit", defaults.TokenLimit, 1),
            DepthLimit = (int)Number("--depth-limit", defaults.DepthLimit, 1),
            InputLimit = Number("--input-limit", defaults.InputLimit),
            InitialBuffer = (int)Number("--initial-buffer", defaults.InitialBuffer, 16),
            BlockRecords = (int)Number("--block-records", defaults.BlockRecords, 1),
            BlockBytes = Number("--block-bytes", defaults.BlockBytes, 1),
        };
    }
}
