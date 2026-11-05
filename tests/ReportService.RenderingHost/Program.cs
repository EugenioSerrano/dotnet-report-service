using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ReportService.Data;
using ReportService.Rendering;
using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.RenderingHost;

/// <summary>
/// One rendering host command: <c>render --fixture &lt;snapshot&gt; --template
/// &lt;package folder&gt; --out &lt;directory&gt; [--mutation &lt;name&gt;]</c>. It reads a
/// snapshot, composes its one block with the product composer, prints it with the
/// product printer into the output folder and writes a JSON result beside the PDF.
/// A named known-bad control changes the composition on the way, so the suite can
/// hold the printed document to what the judge says about it.
/// </summary>
internal static partial class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitFailed = 1;

    /// <summary>The clock the render context of every fixture is fixed to.</summary>
    private static readonly DateTimeOffset FixedClock = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "render", StringComparison.Ordinal))
        {
            return await FailAsync(ExitUsage, Usage).ConfigureAwait(false);
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                return await FailAsync(ExitUsage, $"The option '{args[index]}' holds no value.").ConfigureAwait(false);
            }

            options[args[index][2..]] = args[index + 1];
        }

        foreach (var required in new[] { "fixture", "template", "out" })
        {
            if (!options.ContainsKey(required))
            {
                return await FailAsync(ExitUsage, $"The option '--{required}' is required.").ConfigureAwait(false);
            }
        }

        var mutation = options.TryGetValue("mutation", out var named) ? named : null;
        if (mutation is not null && !KnownBad.Names.Contains(mutation, StringComparer.Ordinal))
        {
            return await FailAsync(ExitUsage, $"The host knows no control '{mutation}'. It knows: {string.Join(", ", KnownBad.Names)}.").ConfigureAwait(false);
        }

        try
        {
            return await RenderAsync(options["fixture"], options["template"], options["out"], mutation).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TemplatePackageException
            or TemplateGuardException or TemplateInputException or PrintTemporarilyFailedException
            or PlaywrightException)
        {
            return await FailAsync(ExitFailed, exception.Message).ConfigureAwait(false);
        }
    }

    private const string Usage =
        "Usage: ReportService.RenderingHost render --fixture <snapshot> --template <package folder> --out <directory> [--mutation <name>]";

    private static async Task<int> RenderAsync(string snapshotPath, string templateFolder, string outFolder, string? mutation)
    {
        Directory.CreateDirectory(outFolder);
        var packageId = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(templateFolder)));
        var manifest = PackageManifests.Of(packageId);
        var files = new FolderTemplatePackageFiles(templateFolder);

        var adapter = new ScribanTemplateAdapter();
        var prepared = adapter.Prepare(manifest, files);

        var (header, records) = await ReadSnapshotAsync(snapshotPath).ConfigureAwait(false);
        var composition = new CompositionRequest(
            prepared,
            new CompositionBlock(Index: 0, RecordCount: records.Count, FirstRecordOrdinal: 1, IsFirst: true, IsLast: true),
            header,
            records,
            new CompositionRender(Culture: null, TimeZone: null, Clock: FixedClock),
            manifest.GroupKey);

        var composer = new BlockComposer(adapter, TimeProvider.System);
        var composed = composer.Compose(composition);
        if (mutation is not null && !KnownBad.IsHtmlLevel(mutation))
        {
            composed = KnownBad.ApplyComposition(mutation, composed);
        }

        // The product's own checks: the units of the composition, then the checks that
        // need no page. A control applied at the page level changes the document after
        // this point, so only the printed text can be wrong - which is what the check
        // after printing is for.
        var text = new TextUnitExtractor().Extract(composed);
        var verifier = new BlockVerifier();
        var anchors = manifest.Pdf.Verification;
        var source = new SourceCheckInput(
            new PartitionFacts(1, records.Count),
            new IndexFacts(0, records.Count, 1),
            new BlockFacts(0, 1, [.. records.Select(record => new BlockRecord(record.Ordinal, record.Values))], header),
            composed,
            text,
            anchors,
            IsFirstBlock: true);
        var sourceCheck = verifier.CheckSource(source, out var sourceChecksMs);

        var fixtureId = Path.GetFileNameWithoutExtension(snapshotPath);
        var name = mutation is null ? fixtureId : $"{fixtureId}-{mutation}";
        var pdfPath = Path.Combine(outFolder, name + ".pdf");

        // The document is printed whatever the source checks said: a control exists to
        // produce a document the judge can read, and a composition the product refuses
        // before printing is still a document the second reader must refuse on its own.
        var html = mutation is not null && KnownBad.IsHtmlLevel(mutation)
            ? KnownBad.ApplyHtml(mutation, composed)
            : composed.Text;

        await using var session = new BrowserSession();
        var printer = new ChromiumPdfPrinter(session, TimeProvider.System);
        var outcome = await printer.PrintAsync(html, files, pdfPath).ConfigureAwait(false);

        string? verificationReason = sourceCheck.Reason;
        var verificationPassed = false;
        var pages = 0;
        var words = 0;
        int? verificationPage = null;
        int? verificationUnit = null;
        int? verificationOffset = null;
        if (sourceCheck.Passed)
        {
            var verification = verifier.Verify(new PrintedCheckInput(
                pdfPath,
                0,
                PlacementConvention.FromMillimetres(manifest.Pdf.MarginMm),
                composed,
                text,
                sourceChecksMs));
            verificationPassed = verification.Passed;
            verificationReason = verification.Reason;
            pages = verification.Pages;
            words = verification.Words;
            verificationPage = verification.Page;
            verificationUnit = verification.UnitIndex;
            verificationOffset = verification.Offset;
        }

        var bytes = await File.ReadAllBytesAsync(pdfPath).ConfigureAwait(false);
        var result = new RenderResult(
            Fixture: fixtureId,
            Template: packageId,
            Mutation: mutation,
            Pdf: Path.GetFileName(pdfPath),
            Sha256: Convert.ToHexStringLower(SHA256.HashData(bytes)),
            SizeBytes: bytes.LongLength,
            AbortedRequests: outcome.AbortedRequests,
            ConsoleErrors: outcome.ConsoleErrors,
            DurationMs: (long)outcome.Duration.TotalMilliseconds,
            VerificationPassed: verificationPassed,
            VerificationReason: verificationReason,
            VerificationPage: verificationPage,
            VerificationUnit: verificationUnit,
            VerificationOffset: verificationOffset,
            Pages: pages,
            Words: words);

        var resultPath = Path.Combine(outFolder, name + ".json");
        await File.WriteAllTextAsync(
            resultPath,
            JsonSerializer.Serialize(result, RenderResultContext.Default.RenderResult) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);

        Console.WriteLine($"rendered {name}: {result.SizeBytes} bytes, sha256 {result.Sha256}");
        return ExitOk;
    }

    private static async Task<(IReadOnlyDictionary<string, object?> Header, IReadOnlyList<ComposedRecord> Records)> ReadSnapshotAsync(string snapshotPath)
    {
        await using var stream = File.OpenRead(snapshotPath);
        IReadOnlyDictionary<string, object?>? header = null;
        var records = new List<ComposedRecord>();
        await foreach (var value in HeaderItemsReader.ReadAsync(stream).ConfigureAwait(false))
        {
            if (value.Ordinal == 0)
            {
                header = RenderContextFactory.Header(value.Json.Span);
                continue;
            }

            records.Add(new ComposedRecord(value.Ordinal, RenderContextFactory.Item(value)));
        }

        return (header ?? throw new InvalidDataException($"{snapshotPath} holds no header."), records);
    }

    private static async Task<int> FailAsync(int exitCode, string message)
    {
        await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
        return exitCode;
    }
}

/// <summary>What one render produced: the document, its hash and what the print saw.</summary>
/// <param name="Fixture">The fixture the block was read from.</param>
/// <param name="Template">The package it was composed with.</param>
/// <param name="Mutation">The control applied, or null for none.</param>
/// <param name="Pdf">The PDF file the print wrote.</param>
/// <param name="Sha256">Its SHA-256, lower-case hexadecimal.</param>
/// <param name="SizeBytes">Its size in bytes.</param>
/// <param name="AbortedRequests">How many requests of the document the route policy aborted.</param>
/// <param name="ConsoleErrors">How many console errors the page reported.</param>
/// <param name="DurationMs">How long the print took, in milliseconds.</param>
/// <param name="VerificationPassed">What the product's verification said about the document.</param>
/// <param name="VerificationReason">The reason its verification refused it with, or null.</param>
/// <param name="VerificationPage">The page its verification stopped on, or null.</param>
/// <param name="VerificationUnit">The unit its verification stopped at, or null.</param>
/// <param name="VerificationOffset">The offset within the page its verification stopped at, or null.</param>
/// <param name="Pages">How many pages the verification read.</param>
/// <param name="Words">How many words the verification read.</param>
internal sealed record RenderResult(
    string Fixture,
    string Template,
    string? Mutation,
    string Pdf,
    string Sha256,
    long SizeBytes,
    int AbortedRequests,
    int ConsoleErrors,
    long DurationMs,
    bool VerificationPassed,
    string? VerificationReason,
    int? VerificationPage,
    int? VerificationUnit,
    int? VerificationOffset,
    int Pages,
    int Words);

/// <summary>The serializer context of the result file, generated at build time.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RenderResult))]
internal sealed partial class RenderResultContext : JsonSerializerContext;

/// <summary>
/// The known-bad controls of the rendering suite: one change, applied to the
/// composition before it is printed, so the printed document is wrong in exactly
/// the way the control names. The suite holds each one to the class the judge
/// reports for it.
/// </summary>
internal static partial class KnownBad
{
    /// <summary>Every control the host knows, by the name the suite selects it with.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "AnchorDoubleEncoded",
        "AnchorTruncated",
        "HeaderAnchorDropped",
        "NC-F",
        "NC-L",
        "NC-R",
        "NC-W",
        "RowDuplicated",
        "RowRemoved",
        "RowWithoutText",
    ];

    /// <summary>Whether the control acts on the printed page rather than on the composition.</summary>
    /// <param name="name">The control's name.</param>
    public static bool IsHtmlLevel(string name) => name is "NC-W" or "NC-L" or "NC-R" or "NC-F";

    /// <summary>
    /// Applies a control of the composition: it acts before the expected units are
    /// extracted, so the reference the checks compare against is wrong too.
    /// </summary>
    /// <param name="name">The control's name.</param>
    /// <param name="block">The composition.</param>
    public static ComposedBlock ApplyComposition(string name, ComposedBlock block)
    {
        if (IsHtmlLevel(name))
        {
            throw new InvalidDataException($"The control '{name}' acts on the printed page, not on the composition.");
        }

        return Apply(name, block);
    }

    /// <summary>
    /// Applies a control of the printed page: the expected units are extracted from the
    /// unchanged composition, so only the document the reader sees is wrong.
    /// </summary>
    /// <param name="name">The control's name.</param>
    /// <param name="block">The composition, unchanged.</param>
    public static string ApplyHtml(string name, ComposedBlock block)
    {
        if (!IsHtmlLevel(name))
        {
            throw new InvalidDataException($"The control '{name}' acts on the composition, not on the printed page.");
        }

        return Apply(name, block).Text;
    }

    /// <summary>Applies the control named <paramref name="name"/> to <paramref name="block"/>.</summary>
    public static ComposedBlock Apply(string name, ComposedBlock block)
    {        ArgumentNullException.ThrowIfNull(block);
        var rows = block.Rows.ToList();
        if (name is not "HeaderAnchorDropped" && rows.Count == 0)
        {
            throw new InvalidDataException($"The control '{name}' needs a block with records.");
        }

        var first = rows[0];
        var last = rows[Math.Min(2, rows.Count - 1)];
        return name switch
        {
            // C-1: a row the partition says exists is not composed.
            "RowRemoved" => Without(block, rows[Math.Min(1, rows.Count - 1)]),
            // C-1: a row is composed twice, under its own ordinal.
            "RowDuplicated" => Twice(block, first),
            // C-3: a row prints no text.
            "RowWithoutText" => Replace(block, first, TextNode().Replace(block.TextOf(first), "><")),
            // C-4: a value printed as it is, truncated by one character.
            "AnchorTruncated" => Replace(block, first, ChangeLongestText(block.TextOf(first), text => text[..^1])),
            // C-4: a value encoded twice, so the page shows the escaped form.
            "AnchorDoubleEncoded" => Replace(block, first, ChangeLongestText(block.TextOf(first), text => text.Replace("&", "&amp;", StringComparison.Ordinal))),
            // C-4: a heading the document start prints is dropped.
            "HeaderAnchorDropped" => ReplaceHeader(block, _ => string.Empty),
            // C-2: a word of a row is not printed.
            "NC-W" => Replace(block, last, ChangeLongestText(block.TextOf(last), DropLastWord)),
            // C-2: a row's text is printed twice.
            "NC-L" => Replace(block, first, ChangeLongestText(block.TextOf(first), text => text + " " + text)),
            // C-2: a row is missing from the printed document.
            "NC-R" => Replace(block, last, string.Empty),
            // C-2: a printed value is altered.
            "NC-F" => Replace(block, first, ChangeLongestText(block.TextOf(first), AlterFirstLetter)),
            _ => throw new InvalidDataException($"The host knows no control '{name}'."),
        };
    }

    // The longest text node of a fragment is the value the row prints as it is: the
    // description. Working on it keeps the markup, the classes and the other cells
    // untouched, so the document is wrong only in the way the control names.
    private static string ChangeLongestText(string html, Func<string, string> change)
    {
        var nodes = TextNode().Matches(html);
        if (nodes.Count == 0)
        {
            throw new InvalidDataException("The fragment prints no text to change.");
        }

        var longest = nodes.OrderByDescending(node => node.Groups[1].Length).First();
        var text = longest.Groups[1].Value;
        return html.Remove(longest.Groups[1].Index, text.Length).Insert(longest.Groups[1].Index, change(text));
    }

    private static string DropLastWord(string text)
    {
        var cut = text.LastIndexOf(' ');
        return cut <= 0 ? text[..^1] : text[..cut];
    }

    private static string AlterFirstLetter(string text)
    {
        var index = 0;
        while (index < text.Length && !char.IsLetter(text[index]))
        {
            index++;
        }

        return index >= text.Length ? text[..^1] + (char)(text[^1] == 'z' ? 'y' : text[^1] + 1) : text[..index] + (char)(text[index] == 'z' ? 'y' : text[index] + 1) + text[(index + 1)..];
    }

    private static ComposedBlock ReplaceHeader(ComposedBlock block, Func<string, string> change)
    {
        var start = block.Fragments.First(fragment => fragment.Kind == FragmentKind.DocumentStart);
        var text = block.TextOf(start);
        if (!HeadingText().IsMatch(text))
        {
            throw new InvalidDataException("The document start prints no heading to change.");
        }

        return Replace(block, start, HeadingText().Replace(text, match => match.Groups[1].Value + change(match.Groups[2].Value) + match.Groups[3].Value, count: 1));
    }
    private static ComposedBlock Replace(ComposedBlock block, ComposedFragment fragment, string replacement)
    {
        using var stream = new MemoryStream(block.Html.Length);
        var fragments = new List<ComposedFragment>(block.Fragments.Count);
        foreach (var current in block.Fragments)
        {
            var text = ReferenceEquals(current, fragment) ? replacement : block.TextOf(current);
            var bytes = Encoding.UTF8.GetBytes(text);
            fragments.Add(current with { Start = (int)stream.Length, Length = bytes.Length });
            stream.Write(bytes);
        }

        return new ComposedBlock(stream.ToArray(), fragments);
    }

    /// <summary>The composition without the fragment: its record is composed nowhere.</summary>
    private static ComposedBlock Without(ComposedBlock block, ComposedFragment fragment) =>
        Rebuild(block, current => ReferenceEquals(current, fragment) ? [] : [block.TextOf(current)]);

    /// <summary>The composition with the fragment again, under its own ordinal.</summary>
    private static ComposedBlock Twice(ComposedBlock block, ComposedFragment fragment) =>
        Rebuild(block, current => ReferenceEquals(current, fragment) ? [block.TextOf(current), block.TextOf(current)] : [block.TextOf(current)]);

    private static ComposedBlock Rebuild(ComposedBlock block, Func<ComposedFragment, IEnumerable<string>> texts)
    {
        using var stream = new MemoryStream(block.Html.Length);
        var fragments = new List<ComposedFragment>(block.Fragments.Count + 1);
        foreach (var current in block.Fragments)
        {
            foreach (var text in texts(current))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                fragments.Add(current with { Start = (int)stream.Length, Length = bytes.Length });
                stream.Write(bytes);
            }
        }

        return new ComposedBlock(stream.ToArray(), fragments);
    }

    private static ComposedBlock ReplaceHeader(ComposedBlock block, Regex pattern, Func<string, string> change)
    {
        var start = block.Fragments.First(fragment => fragment.Kind == FragmentKind.DocumentStart);
        var text = block.TextOf(start);
        var changed = pattern.IsMatch(text)
            ? pattern.Replace(text, match => match.Groups[1].Value + change(match.Groups[2].Value) + match.Groups[3].Value, count: 1)
            : throw new InvalidDataException("The document start prints no heading to change.");
        return Replace(block, start, changed);
    }

    private static ComposedBlock ReplaceWord(ComposedBlock block, ComposedFragment fragment, bool drop)
    {
        var text = block.TextOf(fragment);
        var match = Word().Match(text);
        if (!match.Success)
        {
            throw new InvalidDataException("The row prints no word to change.");
        }

        var word = match.Value;
        var changed = drop
            ? text.Remove(match.Index, word.Length)
            : text.Remove(match.Index, word.Length).Insert(match.Index, Alter(word));
        return Replace(block, fragment, changed);
    }

    private static string Alter(string word) =>
        word.All(char.IsAsciiDigit)
            ? (word[0] == '9' ? '8' : (char)(word[0] + 1)) + word[1..]
            : new string([(char)(word[0] == 'z' ? 'y' : word[0] + 1), .. word.Skip(1)]);

    private static string DuplicateTextNode(string row)
    {
        var match = TextNode().Match(row);
        return match.Success
            ? row.Insert(match.Index + match.Length, match.Value)
            : throw new InvalidDataException("The row prints no text to repeat.");
    }

    // The heading of the document start: the text between its opening and closing tag.
    [GeneratedRegex("(<h1>)([^<]*)(</h1>)", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingText();

    // A cell's text, between the tags that hold it.
    [GeneratedRegex(">([^<>]+)<", RegexOptions.CultureInvariant)]
    private static partial Regex TextNode();

    // Every text node of a fragment, so a row can be emptied of its text.
    [GeneratedRegex(">([^<>]+)<", RegexOptions.CultureInvariant)]
    private static partial Regex TextNodes();

    // A word of at least three letters or digits, away from the markup.
    [GeneratedRegex("[A-Za-z0-9]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}
