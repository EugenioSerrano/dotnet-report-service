using System.Text.Json.Nodes;

namespace ReportService.PdfOracle;

/// <summary>
/// The judge's one command: <c>verdict --pdf &lt;file&gt; --fixture &lt;snapshot&gt;
/// --expectation &lt;declaration&gt; [--out &lt;verdict&gt;]</c>. It prints one verdict, or
/// writes it to the named file, and exits 0 — a document that fails a rule is a
/// verdict, not an instrument error. A document the text tools cannot read exits 1,
/// and a command the program does not understand exits 2. A caller that cancels the
/// run gets no verdict and no exit code of the program's own: the cancellation
/// propagates, because a run that was stopped is neither a verdict nor an error of
/// the instrument.
/// </summary>
internal static class Program
{
    private const int ExitVerdict = 0;
    private const int ExitUnreadable = 1;
    private const int ExitUsage = 2;

    /// <summary>Runs the command as the image does: the text tools of the instrument, on the console.</summary>
    public static Task<int> Main(string[] args) =>
        RunAsync(args, PopplerText.ReadAsync, Console.Out, Console.Error);

    /// <summary>
    /// The reading the verdict was reached from, in the order the tool reports the
    /// words: the pages it covers and, for every word, the page, the characters and
    /// the four edges of its box. Two readings of the same document compare equal
    /// word for word, and the continuation of a split row is asserted on this order.
    /// </summary>
    private static string ReadingJson(string file, PdfExtraction extraction)
    {
        var words = new JsonArray();
        foreach (var word in extraction.Words)
        {
            words.Add(new JsonObject
            {
                ["page"] = word.Page,
                ["text"] = word.Text,
                ["box"] = new JsonArray(word.XMin, word.YMin, word.XMax, word.YMax),
            });
        }

        return new JsonObject
        {
            ["file"] = file,
            ["pages"] = extraction.Pages,
            ["words"] = words,
        }.ToJsonString();
    }

    /// <summary>
    /// Runs the command with the reading, the two writers and the caller's
    /// cancellation given: the tool the image starts is replaced by
    /// <paramref name="read"/> in the judge's own tests, which is how the option
    /// parsing and the three exit codes are held without a PDF tool on the host.
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        Func<string, CancellationToken, Task<PdfExtraction>> read,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0 || !string.Equals(args[0], "verdict", StringComparison.Ordinal))
        {
            await error.WriteLineAsync("Usage: ReportService.PdfOracle verdict --pdf <file> --fixture <snapshot> --expectation <declaration> [--out <verdict>] [--reading <reading>]").ConfigureAwait(false);
            return ExitUsage;
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                await error.WriteLineAsync($"The option '{args[index]}' holds no value.").ConfigureAwait(false);
                return ExitUsage;
            }

            options[args[index][2..]] = args[index + 1];
        }

        foreach (var required in new[] { "pdf", "fixture", "expectation" })
        {
            if (!options.ContainsKey(required))
            {
                await error.WriteLineAsync($"The option '--{required}' is required.").ConfigureAwait(false);
                return ExitUsage;
            }
        }

        try
        {
            var fixture = FixtureSource.Load(options["fixture"]);
            var expectation = ExpectationDocument.Load(options["expectation"]);
            var extraction = await read(options["pdf"], cancellationToken).ConfigureAwait(false);
            var verdict = IntegrityRules.Judge(extraction, fixture, expectation, Path.GetFileName(options["pdf"]));
            var json = verdict.ToJson();
            if (options.TryGetValue("out", out var destination))
            {
                await File.WriteAllTextAsync(destination, json + "\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync(json).ConfigureAwait(false);
            }

            if (options.TryGetValue("reading", out var readingPath))
            {
                await File.WriteAllTextAsync(
                    readingPath,
                    ReadingJson(Path.GetFileName(options["pdf"]), extraction) + "\n",
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
            }

            return ExitVerdict;
        }
        catch (OracleInstrumentException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ExitUnreadable;
        }
        catch (InvalidDataException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ExitUnreadable;
        }
        catch (System.Text.Json.JsonException exception)
        {
            // A snapshot or a declaration that is not the document it claims to be: the
            // file cannot be read, and a run that cannot read it has no verdict to give.
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ExitUnreadable;
        }
        catch (IOException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ExitUnreadable;
        }
    }
}
