using System.Globalization;
using ReportService.TemplateChecks.PdfConstraints;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// The tool's command line: <c>scaffold</c> and <c>render</c>, their options and the
/// four exit codes (0 success, 1 refused, 2 usage, 3 could not run). The result lines
/// go to standard output; a usage error and the usage block go to standard error. A
/// command never writes anything but its own line: a refusal never carries template
/// text, a sample value or a host path. A render that succeeded may print authoring
/// warnings right before its result line; a warning names a package-relative file or a
/// part and at most a short, escaped value of the author's own sample, and never changes
/// the exit code or the files written.
/// </summary>
internal static class Program
{
    private const int ExitUsage = 2;
    private const int ExitUnavailable = 3;

    private const string Usage = """
        Scaffolds a template package for one engine and renders a package's own sample locally.

        Usage:
          report-template scaffold --engine <scriban|handlebars|liquid> --id <template-id> [--version <semver>] [--out <dir>]
          report-template render <package-folder> [--out <dir>] [--page-capacity <pages>] [--data <file>]
          report-template watch <package-folder> [--out <dir>] [--page-capacity <pages>] [--data <file>]

        Defaults: --version 1.0.0, --out <current directory>/<id> for scaffold and
        <current directory>/artifacts/template-preview/<id>/<version> for render;
        --page-capacity the shipped example profile's page capacity - pass the target
        deployment's configured DocumentCapacity:MaximumPages to check against it.

        Exit codes: 0 success, 1 refused by a product check or the sample render,
        2 invalid command line, 3 the render could not run.
        A render that succeeded may print 'warning:' lines before its result line;
        a warning never changes the exit code.
        """;

    /// <summary>Runs one command and returns its exit code.</summary>
    public static async Task<int> Main(string[] args)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var command = args.Length > 0 ? args[0] : "(none)";
        int code;
        try
        {
            var tool = new TemplateAuthoringTool();
            code = command switch
            {
                "scaffold" => Scaffold(tool, args, output),
                "render" => await RenderAsync(args, output).ConfigureAwait(false),
                "watch" => await WatchAsync(args, output).ConfigureAwait(false),
                _ => Fail(output, args.Length == 0
                    ? "error: a command is required."
                    : "error: the command '" + args[0] + "' is not one this tool knows."),
            };
        }
        catch (UsageException exception)
        {
            code = Fail(output, "error: " + exception.Message);
        }
        catch (Exception exception)
        {
            // An unexpected exception prints its type and the operation it happened in,
            // never its message: a message can carry a path or template text.
            code = ExitUnavailable;
            output.WriteLine("internal-error: " + exception.GetType().Name + " in " + command);
        }

        if (code == ExitUsage)
        {
            await Console.Error.WriteAsync(output.ToString()).ConfigureAwait(false);
            await Console.Error.WriteLineAsync().ConfigureAwait(false);
            await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
        }
        else
        {
            await Console.Out.WriteAsync(output.ToString()).ConfigureAwait(false);
        }

        return code;
    }

    private static int Scaffold(TemplateAuthoringTool tool, string[] args, TextWriter output)
    {
        var line = CommandLine.Parse(args, ["--engine", "--id", "--version", "--out"], positionalCount: 0);
        var engine = line.Get("--engine") ?? throw new UsageException("--engine is required.");
        var id = line.Get("--id") ?? throw new UsageException("--id is required.");
        var target = line.Get("--out") ?? Path.Combine(Directory.GetCurrentDirectory(), id);
        return tool.Scaffold(new ScaffoldRequest(engine, id, target, line.Get("--version") ?? "1.0.0"), output);
    }

    private static Task<int> RenderAsync(string[] args, TextWriter output)
    {
        var (tool, request) = RenderCommand(args);
        return tool.RenderAsync(request, output);
    }

    // The watch writes each render's lines as it happens; only a usage error on the first
    // render goes through the buffered output, so it is printed with the usage block.
    private static async Task<int> WatchAsync(string[] args, TextWriter output)
    {
        var (tool, request) = RenderCommand(args);
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, press) =>
        {
            press.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await new PackageWatcher(tool)
                .RunAsync(request, Console.Out, stop.Token, usageOutput: output)
                .ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static (TemplateAuthoringTool Tool, RenderRequest Request) RenderCommand(string[] args)
    {
        var line = CommandLine.Parse(args, ["--out", PageCapacityOption.Name, "--data"], positionalCount: 1);
        NumberingCapacity? capacity;
        try
        {
            capacity = PageCapacityOption.Parse(line.Get(PageCapacityOption.Name));
        }
        catch (ArgumentException exception)
        {
            throw new UsageException(exception.Message);
        }

        return (
            new TemplateAuthoringTool(capacity: capacity),
            new RenderRequest(line.Positional[0], line.Get("--out"), line.Get("--data")));
    }

    private static int Fail(TextWriter output, string message)
    {
        output.WriteLine(message);
        return ExitUsage;
    }
}

/// <summary>Invalid command-line arguments (exit code 2).</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>The parsed options and positional arguments of one command.</summary>
/// <param name="Options">The option values, by their <c>--name</c>.</param>
/// <param name="Positional">The positional arguments, in order.</param>
internal sealed record CommandLine(IReadOnlyDictionary<string, string> Options, IReadOnlyList<string> Positional)
{
    /// <summary>Parses <c>--name value</c> pairs and exactly <paramref name="positionalCount"/> positional arguments.</summary>
    /// <param name="args">The raw command line, the command at index 0.</param>
    /// <param name="allowed">The options the command accepts.</param>
    /// <param name="positionalCount">How many positional arguments the command takes.</param>
    public static CommandLine Parse(string[] args, IReadOnlyList<string> allowed, int positionalCount)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(argument);
                continue;
            }

            if (!allowed.Contains(argument, StringComparer.Ordinal))
            {
                throw new UsageException("the option '" + argument + "' is not one '" + args[0] + "' knows (" + string.Join(", ", allowed) + ").");
            }

            if (index + 1 >= args.Length)
            {
                throw new UsageException("the option '" + argument + "' needs a value.");
            }

            if (!options.TryAdd(argument, args[++index]))
            {
                throw new UsageException("the option '" + argument + "' is given more than once.");
            }
        }

        if (positional.Count != positionalCount)
        {
            throw new UsageException("'" + args[0] + "' takes " + positionalCount + " positional argument(s), " + positional.Count + " given.");
        }

        return new CommandLine(options, positional);
    }

    /// <summary>The option's value, or <see langword="null"/> when it is not given.</summary>
    /// <param name="name">The option's name, with its two dashes.</param>
    public string? Get(string name) => Options.TryGetValue(name, out var value) ? value : null;
}
