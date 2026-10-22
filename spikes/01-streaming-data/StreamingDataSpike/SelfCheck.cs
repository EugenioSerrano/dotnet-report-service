using System.Text;
using System.Text.RegularExpressions;
using StreamingDataSpike.Measuring;
using StreamingDataSpike.Parsing;
using StreamingDataSpike.Rendering;

namespace StreamingDataSpike;

/// <summary>
/// The repeatable check of this measurement program: hostile inputs give
/// their exact outcomes, every pilot file parses with totals equal to the
/// header, the three engines give byte-identical HTML with the hostile
/// values escaped and inert, and the culture and parallel variants give the
/// same hashes. Exit 0 only when every check passes.
/// </summary>
internal static partial class SelfCheck
{
    private const long Rss256MiB = 256L * 1_048_576;

    private static readonly string[] PilotFiles =
    [
        "header-items-v1.simple.25.json",
        "header-items-v1.grouped.10k.json",
        "header-items-v1.volume.100k.json",
        "header-items-v1.volume.500k.json",
        "header-items-v1.volume.1m.json",
    ];

    public static async Task<int> RunAsync(Arguments arguments)
    {
        var datasets = Path.GetFullPath(arguments.Required("--datasets"));
        var hostile = arguments.Option("--hostile") ?? Path.Combine(Path.GetDirectoryName(datasets)!, "hostile");
        var output = arguments.Option("--output");
        arguments.EnsureAllUsed(0);

        var checks = new List<(string Name, bool Pass, string Detail)>();
        void Check(string name, bool pass, string detail = "")
        {
            checks.Add((name, pass, detail));
            Console.Out.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  " + detail : string.Empty)}");
        }

        // 1. Hostile inputs, each in its own process.
        await HostileInputs.WriteAllAsync(hostile, Path.Combine(datasets, "header-items-v1.volume.100k.json")).ConfigureAwait(false);
        var hostileLines = new List<string>();
        foreach (var hostileCase in HostileInputs.Cases)
        {
            var child = await ChildProcess.RunAsync(["parse", Path.Combine(hostile, hostileCase.File), .. hostileCase.Options]).ConfigureAwait(false);
            var (pass, observed) = Evaluate(hostileCase, child);
            hostileLines.Add(HostileReport.Line(hostileCase, child, pass, observed));
            Check($"hostile {hostileCase.Id} ({hostileCase.File})", pass, observed);
        }

        // 2. Every pilot file, plain and gzip, parses with totals equal to the header.
        foreach (var name in PilotFiles)
        {
            foreach (var suffix in new[] { string.Empty, ".gz" })
            {
                var run = new ParseRun();
                string detail;
                bool pass;
                try
                {
                    await run.ExecuteAsync(Path.Combine(datasets, name + suffix), new EnvelopeLimits(), null, CancellationToken.None).ConfigureAwait(false);
                    pass = true;
                    detail = $"records={run.Validator!.Records} groups={run.Validator.GroupRuns} blocks={run.Validator.Blocks} amount={run.Validator.Amount} maxBuffer={run.Reader!.MaxBufferBytes}";
                }
                catch (EnvelopeException exception)
                {
                    pass = false;
                    detail = exception.ToErrorLine();
                }

                Check($"parse {name}{suffix} ok, totals equal the header", pass, detail);
            }
        }

        // Growth path: a 16-byte first buffer gives the same result.
        {
            var file = Path.Combine(datasets, "header-items-v1.grouped.10k.json");
            var normal = new ParseRun();
            await normal.ExecuteAsync(file, new EnvelopeLimits(), null, CancellationToken.None).ConfigureAwait(false);
            var tiny = new ParseRun();
            await tiny.ExecuteAsync(file, new EnvelopeLimits { InitialBuffer = 16 }, null, CancellationToken.None).ConfigureAwait(false);
            Check(
                "parse grouped.10k with --initial-buffer 16 equals the default",
                tiny.Validator!.Records == normal.Validator!.Records && tiny.Validator.Amount == normal.Validator.Amount && tiny.Validator.GroupRuns == normal.Validator.GroupRuns && tiny.Validator.Blocks == normal.Validator.Blocks,
                $"records={tiny.Validator.Records} groups={tiny.Validator.GroupRuns} blocks={tiny.Validator.Blocks} maxBuffer={tiny.Reader!.MaxBufferBytes} (default maxBuffer={normal.Reader!.MaxBufferBytes})");
        }

        // 3. Byte identity and escaping assertions.
        var compareLines = new List<string>();
        string? groupedHashes = null;
        foreach (var (file, template) in new[]
        {
            ("header-items-v1.simple.25.json", "pilot-simple"),
            ("header-items-v1.grouped.10k.json", "pilot-grouped"),
            ("header-items-v1.volume.100k.json", "pilot-volume"),
        })
        {
            var assertions = new EscapingAssertions();
            var result = await Commands.CompareCore(Path.Combine(datasets, file), template, null, 1, new EnvelopeLimits(), assertions.Observe).ConfigureAwait(false);
            compareLines.Add(result.Line);
            Check($"compare {template} x {file}: zero mismatches, three equal hashes", result.Identical, $"records={result.Records} sha256={result.Sha256}");
            Check($"escaping {template} x {file}: hostile values escaped, never raw, template-looking values literal", assertions.HostileRows > 0 && assertions.Failures == 0, $"hostileRows={assertions.HostileRows} nonAsciiRows={assertions.NonAsciiRows} failures={assertions.Failures}{(assertions.FirstFailure is null ? string.Empty : " first=" + assertions.FirstFailure)}");
            if (template == "pilot-grouped")
            {
                groupedHashes = HashesOf(result.Line);
            }
        }

        // 4. Culture and concurrency variants on grouped.10k.
        foreach (var (culture, parallel) in new[] { ("es-AR", 1), ((string?)null, 4) })
        {
            var result = await Commands.CompareCore(Path.Combine(datasets, "header-items-v1.grouped.10k.json"), "pilot-grouped", culture, parallel, new EnvelopeLimits(), null).ConfigureAwait(false);
            compareLines.Add(result.Line);
            Check($"compare pilot-grouped x grouped.10k with culture={culture ?? "invariant"} parallel={parallel}: same three hashes", result.Identical && HashesOf(result.Line) == groupedHashes, $"sha256={result.Sha256}");
        }

        if (output is not null)
        {
            Directory.CreateDirectory(output);
            await WriteLinesAsync(Path.Combine(output, "hostile-runs.jsonl"), hostileLines).ConfigureAwait(false);
            await WriteLinesAsync(Path.Combine(output, "compare.jsonl"), compareLines).ConfigureAwait(false);
        }

        var failed = checks.Count(check => !check.Pass);
        Console.Out.WriteLine($"selfcheck: {checks.Count - failed} of {checks.Count} checks PASS");
        return failed == 0 ? 0 : 1;
    }

    private static (bool Pass, string Observed) Evaluate(HostileCase expected, ChildResult child)
    {
        var values = Bench.Values(child.StandardOutput);
        var errorLine = child.StandardError.Trim();
        var match = ErrorLinePattern().Match(errorLine);
        var observed = child.ExitCode == 0 ? "exit 0 (ok)" : $"exit {child.ExitCode} {errorLine}";
        var rss = values.GetValueOrDefault("processPeakRssBytes") as decimal? ?? 0m;
        var records = values.GetValueOrDefault("records") as decimal? ?? 0m;
        var maxBuffer = values.GetValueOrDefault("maxBufferBytes") as decimal? ?? 0m;
        observed += $" peakRss={rss / 1_048_576m:F1}MiB records={records} maxBuffer={maxBuffer}";

        if (expected.Id == "H12")
        {
            // Either outcome is acceptable; the observed one is recorded.
            var ok = child.ExitCode == 0 && values.GetValueOrDefault("status") as string == "ok";
            var token = child.ExitCode == 3 && match.Success && match.Groups["code"].Value == ErrorCodes.QuotaExceeded && match.Groups["detail"].Value == "limit=token";
            return (ok || token, observed);
        }

        var pass = child.ExitCode == expected.ExitCode
            && match.Success
            && match.Groups["code"].Value == expected.Code
            && match.Groups["detail"].Value == expected.Detail
            && (expected.Ordinal is null || match.Groups["ordinal"].Value == expected.Ordinal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            && values.GetValueOrDefault("status") as string == "error";

        if (expected.Id == "H5")
        {
            pass &= rss > 0 && rss < Rss256MiB;
        }

        if (expected.Id == "H6" && match.Success)
        {
            pass &= long.Parse(match.Groups["offset"].Value, System.Globalization.CultureInfo.InvariantCulture) <= 33_554_432 + (long)maxBuffer;
        }

        if (expected.Id == "H7")
        {
            pass &= records > 0;
        }

        return (pass, observed);
    }

    private static string? HashesOf(string line)
    {
        var values = Bench.Values(line);
        return $"{values.GetValueOrDefault("sha256Scriban")}/{values.GetValueOrDefault("sha256Handlebars")}/{values.GetValueOrDefault("sha256Liquid")}";
    }

    private static async Task WriteLinesAsync(string path, IEnumerable<string> lines)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        foreach (var line in lines)
        {
            await writer.WriteLineAsync(line.Trim()).ConfigureAwait(false);
        }
    }

    /// <summary>The only shape of a data error line: codes, names and numbers, no path and no data value.</summary>
    [GeneratedRegex(@"^error code=(?<code>[A-Z_]+) (?<detail>(limit|reason)=[a-z\-]+) ordinal=(?<ordinal>\d+) offset=(?<offset>\d+)$")]
    private static partial Regex ErrorLinePattern();

    /// <summary>Checks every rendered row whose description holds a hostile or non-ASCII value.</summary>
    private sealed class EscapingAssertions
    {
        public long HostileRows { get; private set; }

        public long NonAsciiRows { get; private set; }

        public long Failures { get; private set; }

        public string? FirstFailure { get; private set; }

        public void Observe(EnvelopeRecord record, string row)
        {
            if (record.Values.GetValueOrDefault("description") is not string description)
            {
                return;
            }

            var encoded = HtmlPolicy.Encode(description);
            var hostile = Probes.HostileValues.Any(value => description.Contains(value, StringComparison.Ordinal));
            var nonAscii = description.Any(c => c > 127);
            if (hostile)
            {
                HostileRows++;
                var special = description.IndexOfAny(['<', '>', '&', '"', '\'']) >= 0;
                if (!row.Contains(encoded, StringComparison.Ordinal) || (special && row.Contains(description, StringComparison.Ordinal)))
                {
                    Fail(record, "hostile value not escaped by the policy");
                }
            }

            if (nonAscii)
            {
                NonAsciiRows++;
                if (!row.Contains(encoded, StringComparison.Ordinal))
                {
                    Fail(record, "non-ASCII value not written as the policy defines");
                }
            }
        }

        private void Fail(EnvelopeRecord record, string what)
        {
            Failures++;
            FirstFailure ??= $"record {record.Ordinal}: {what}";
        }
    }
}
