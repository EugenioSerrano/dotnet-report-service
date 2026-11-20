using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Playwright;
using ReportService.Data;
using ReportService.Rendering;
using ReportService.Pdf.Verification;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Manifest;
using ReportService.Templates;
using ReportService.Workers.Pdf.Templates;

namespace ReportService.RenderingHost;

/// <summary>
/// The rendering host's two commands. <c>render --fixture &lt;snapshot&gt; --template
/// &lt;package folder&gt; --out &lt;directory&gt; [--mutation &lt;name&gt;]</c> reads a
/// snapshot, composes its one block with the product composer, prints it with the
/// product printer into the output folder and writes a JSON result beside the PDF; a
/// named known-bad control changes the composition on the way. <c>sample --template
/// &lt;package folder&gt; --out &lt;directory&gt;</c> has no fixture: it renders the
/// package's own <c>sample.json</c> through the take's sample renderer - the same
/// composer, printer and verification - keeps the verified PDF and writes the same
/// kind of result, so the demo prints what the take gates on.
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
        if (args.Length > 0 && string.Equals(args[0], "sample", StringComparison.Ordinal))
        {
            return await SampleAsync(args).ConfigureAwait(false);
        }

        if (args.Length == 0 || !string.Equals(args[0], "render", StringComparison.Ordinal))
        {
            return await FailAsync(ExitUsage, Usage).ConfigureAwait(false);
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var pageSettingsUnset = false;
        for (var index = 1; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--page-settings-unset", StringComparison.Ordinal))
            {
                pageSettingsUnset = true;
                continue;
            }

            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                return await FailAsync(ExitUsage, $"The option '{args[index]}' holds no value.").ConfigureAwait(false);
            }

            if (!KnownOptions.Contains(args[index][2..], StringComparer.Ordinal))
            {
                return await FailAsync(ExitUsage, $"The option '{args[index]}' is not one this host knows ({string.Join(", ", KnownOptions)}).").ConfigureAwait(false);
            }

            options[args[index][2..]] = args[index + 1];
            index++;
        }

        foreach (var required in new[] { "fixture", "template", "out" })
        {
            if (!options.ContainsKey(required))
            {
                return await FailAsync(ExitUsage, $"The option '--{required}' is required.").ConfigureAwait(false);
            }
        }

        var mutation = options.TryGetValue("mutation", out var named) ? named : null;
        var stem = options.TryGetValue("name", out var givenName) ? givenName : null;
        if (mutation is not null && !KnownBad.Names.Contains(mutation, StringComparer.Ordinal))
        {
            return await FailAsync(ExitUsage, $"The host knows no control '{mutation}'. It knows: {string.Join(", ", KnownBad.Names)}.").ConfigureAwait(false);
        }

        if (options.TryGetValue("ambient-culture", out var ambientCulture))
        {
            if (!AmbientCultures.Contains(ambientCulture, StringComparer.Ordinal))
            {
                return await FailAsync(ExitUsage, $"The host knows no ambient culture '{ambientCulture}'. It knows: {string.Join(", ", AmbientCultures)}.").ConfigureAwait(false);
            }

            var culture = string.Equals(ambientCulture, "invariant", StringComparison.Ordinal)
                ? CultureInfo.InvariantCulture
                : CultureInfo.GetCultureInfo(ambientCulture);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        try
        {
            return await RenderAsync(options["fixture"], options["template"], options["out"], mutation, stem, pageSettingsUnset).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TemplatePackageException
            or TemplateGuardException or TemplateInputException or PrintTemporarilyFailedException
            or PlaywrightException or PresentationRefusedException or PresentationUnavailable)
        {
            return await FailAsync(ExitFailed, exception.Message).ConfigureAwait(false);
        }
    }

    private const string Usage =
        "Usage: ReportService.RenderingHost render --fixture <snapshot> --template <package folder> --out <directory> [--mutation <name>] [--name <stem>] [--ambient-culture <invariant|es-AR>] [--page-settings-unset]\n"
        + "       ReportService.RenderingHost sample --template <package folder> --out <directory> [--name <stem>]";

    /// <summary>The options one render accepts; anything else is a usage error, never silently ignored.</summary>
    private static readonly IReadOnlyList<string> KnownOptions = ["fixture", "template", "out", "mutation", "name", "ambient-culture"];

    /// <summary>The options the sample verb accepts: no fixture, because the package's own sample is the data.</summary>
    private static readonly IReadOnlyList<string> SampleOptions = ["template", "out", "name"];

    /// <summary>
    /// The culture the composing process itself runs under, when a case names one: the
    /// conformance threshold asks for the same bytes under <c>es-AR</c> and under the invariant
    /// culture, so the host can be made to run as a machine of either. It is the process's own
    /// culture - never a render setting - and the report's presentation must not follow it.
    /// </summary>
    private static readonly IReadOnlyList<string> AmbientCultures = ["invariant", "es-AR"];



    private static async Task<int> RenderAsync(
        string snapshotPath,
        string templateFolder,
        string outFolder,
        string? mutation,
        string? stem,
        bool pageSettingsUnset)
    {
        Directory.CreateDirectory(outFolder);
        var packageId = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(templateFolder)));
        var manifest = ManifestOf(templateFolder);
        if (pageSettingsUnset)
        {
            // The manifest copy the renderer receives loses outputs.pdf.paper, as a take
            // defect would leave it: the renderer's own guard is what is being held.
            manifest = manifest with { PdfOutput = JsonDocument.Parse("{}").RootElement.Clone() };
        }

        var fixtureId = Path.GetFileNameWithoutExtension(snapshotPath);
        var name = stem ?? (mutation is null ? fixtureId : $"{fixtureId}-{mutation}");

        // The page is resolved before anything is composed: an absent or unusable page
        // setting prints nothing, and no page is guessed.
        var pageSettings = PdfPageSettingsRules.Resolve(manifest.PdfOutput);
        if (pageSettings.Settings is not { } settings)
        {
            var refusal = new RenderResult(
                Fixture: fixtureId,
                Template: packageId,
                Mutation: mutation,
                Pdf: null,
                Sha256: null,
                HtmlSha256: null,
                SizeBytes: 0,
                AbortedRequests: 0,
                ConsoleErrors: 0,
                DurationMs: 0,
                VerificationPassed: false,
                VerificationReason: VerificationReasons.PageSettingsInvalid,
                VerificationPage: null,
                VerificationUnit: null,
                VerificationOffset: null,
                VerificationField: pageSettings.UnusableField,
                VerificationMs: null,
                Pages: 0,
                Words: 0);
            await WriteResultAsync(outFolder, name, refusal).ConfigureAwait(false);
            Console.WriteLine($"refused {name}: page settings invalid at {pageSettings.UnusableField}");
            return ExitOk;
        }

        var pageRule = PageRule.From(settings);
        var frame = PageFrame.FromMillimetres(
            (double)settings.WidthMm,
            (double)settings.HeightMm,
            (double)settings.MarginTopMm,
            (double)settings.MarginRightMm,
            (double)settings.MarginBottomMm,
            (double)settings.MarginLeftMm);
        var files = new FolderTemplatePackageFiles(templateFolder);

        var decision = EngineResolution.Resolve(
            manifest,
            pinEngine: null,
            TemplateEngineAdapters.Registry());
        if (decision.Adapter is not { } adapter)
        {
            throw new InvalidDataException(decision.Message);
        }

        var prepared = adapter.Prepare(manifest, files);

        var (header, records) = await ReadSnapshotAsync(snapshotPath).ConfigureAwait(false);
        var culture = manifest.LocaleDefaults.Culture;
        var timeZone = manifest.LocaleDefaults.TimeZone;
        if (string.IsNullOrWhiteSpace(culture) || string.IsNullOrWhiteSpace(timeZone))
        {
            throw new InvalidDataException("The package declares no culture and time zone to present under.");
        }

        var composition = new CompositionRequest(
            prepared,
            new CompositionBlock(Index: 0, RecordCount: records.Count, FirstRecordOrdinal: 1, IsFirst: true, IsLast: true),
            header,
            records,
            new CompositionRender(Culture: culture, TimeZone: timeZone, Clock: FixedClock),
            manifest.GroupKey);

        var composer = new BlockComposer(TimeProvider.System);
        var composed = await composer.ComposeAsync(
            composition,
            adapter,
            PresentationSettingsResolver.Resolve(new PresentationValues(
                composition.Render.Culture,
                composition.Render.TimeZone,
                composition.Render.Clock)));
        if (string.Equals(mutation, InvariantCulturePresentation.ControlName, StringComparison.Ordinal))
        {
            composed = new InvariantCulturePresentation(records, header, timeZone, FixedClock).Apply(composed);
        }
        else if (mutation is not null && !KnownBad.IsHtmlLevel(mutation))
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

        var pdfPath = Path.Combine(outFolder, name + ".pdf");

        // The document is printed whatever the source checks said: a control exists to
        // produce a document the judge can read, and a composition the product refuses
        // before printing is still a document the second reader must refuse on its own.
        var document = mutation is not null && KnownBad.IsHtmlLevel(mutation) && !KnownBad.AppendsStylesheet(mutation)
            ? KnownBad.ApplyHtml(mutation, composed)
            : composed.Text;
        // A control's stylesheet is a package file linked before the page rule, the way a
        // package's own stylesheet is linked from the document. Chromium 153 resolves the
        // page's size from the first important declaration in document order, and a later
        // one loses - measured on the pinned base: a package that declares
        // `@page { size: ... !important; }` prints its own page only when its rule comes
        // before the service's. The page rule stays the last element of the served document.
        if (mutation is not null && KnownBad.AppendsStylesheet(mutation))
        {
            document += KnownBad.StylesheetLink(mutation);
        }

        var served = pageRule.AppendTo(document);

        await using var session = new BrowserSession();
        var printer = new ChromiumPdfPrinter(session, TimeProvider.System);
        var outcome = await printer.PrintAsync(served, pageRule, files, pdfPath).ConfigureAwait(false);

        string? verificationReason = sourceCheck.Reason;
        var verificationPassed = false;
        var pages = 0;
        var words = 0;
        int? verificationPage = null;
        int? verificationUnit = null;
        int? verificationOffset = null;
        string? verificationField = null;
        long? verificationMs = null;
        if (sourceCheck.Passed)
        {
            var verification = verifier.Verify(new PrintedCheckInput(
                pdfPath,
                0,
                frame,
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
            verificationField = verification.Field;
            verificationMs = verification.ReadAndAlignMs;
        }

        var bytes = await File.ReadAllBytesAsync(pdfPath).ConfigureAwait(false);
        var result = new RenderResult(
            Fixture: fixtureId,
            Template: packageId,
            Mutation: mutation,
            Pdf: Path.GetFileName(pdfPath),
            Sha256: Convert.ToHexStringLower(SHA256.HashData(bytes)),
            HtmlSha256: Convert.ToHexStringLower(SHA256.HashData(composed.Html)),
            SizeBytes: bytes.LongLength,
            AbortedRequests: outcome.AbortedRequests,
            ConsoleErrors: outcome.ConsoleErrors,
            DurationMs: (long)outcome.Duration.TotalMilliseconds,
            VerificationPassed: verificationPassed,
            VerificationReason: verificationReason,
            VerificationPage: verificationPage,
            VerificationUnit: verificationUnit,
            VerificationOffset: verificationOffset,
            VerificationField: verificationField,
            VerificationMs: verificationMs,
            Pages: pages,
            Words: words);

        await WriteResultAsync(outFolder, name, result).ConfigureAwait(false);
        Console.WriteLine($"rendered {name}: {result.SizeBytes} bytes, sha256 {result.Sha256}");
        return ExitOk;
    }

    private static async Task WriteResultAsync(string outFolder, string name, RenderResult result)
    {
        var resultPath = Path.Combine(outFolder, name + ".json");
        await File.WriteAllTextAsync(
            resultPath,
            JsonSerializer.Serialize(result, RenderResultContext.Default.RenderResult) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);
    }

    private static async Task<int> SampleAsync(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                return await FailAsync(ExitUsage, $"The option '{args[index]}' holds no value.").ConfigureAwait(false);
            }

            if (!SampleOptions.Contains(args[index][2..], StringComparer.Ordinal))
            {
                return await FailAsync(ExitUsage, $"The option '{args[index]}' is not one the sample verb knows ({string.Join(", ", SampleOptions)}).").ConfigureAwait(false);
            }

            options[args[index][2..]] = args[index + 1];
            index++;
        }

        foreach (var required in new[] { "template", "out" })
        {
            if (!options.ContainsKey(required))
            {
                return await FailAsync(ExitUsage, $"The option '--{required}' is required.").ConfigureAwait(false);
            }
        }

        try
        {
            return await RenderSampleAsync(
                options["template"],
                options["out"],
                options.TryGetValue("name", out var stem) ? stem : null).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TemplatePackageException
            or TemplateInputException or PrintTemporarilyFailedException or PlaywrightException)
        {
            return await FailAsync(ExitFailed, exception.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The manifest the renderer reads, from the package's own <c>template.yaml</c>: the host
    /// states no manifest of its own, so a package that changes cannot be rendered as a
    /// declaration it does not make. The page settings travel as the file stores them, and
    /// the engine version is the adapter's own - the same mapping the take's renderer uses.
    /// </summary>
    /// <param name="templateFolder">The package folder the host mounted.</param>
    private static RenderManifest ManifestOf(string templateFolder)
    {
        TemplateManifest parsed;
        using (var stream = File.OpenRead(Path.Combine(templateFolder, "template.yaml")))
        {
            var read = new TemplateManifestReader().Read(stream);
            if (!read.Succeeded || read.Manifest is null)
            {
                throw new InvalidDataException(read.Reason ?? $"{templateFolder} holds no readable template.yaml.");
            }

            parsed = read.Manifest;
        }

        var engine = parsed.Engine
            ?? throw new InvalidDataException($"{templateFolder}'s template.yaml declares no engine.");
        var declared = TemplateEngineNames.ToManifestName(engine);
        var adapter = TemplateEngineAdapters.All()
            .SingleOrDefault(candidate => string.Equals(candidate.Engine, declared, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"{templateFolder}'s template.yaml declares the engine '{declared}', which this host does not run.");
        try
        {
            return RenderManifestMapping.From(parsed, engine, adapter.Version);
        }
        catch (TemplatePackageException)
        {
            // The delivered fixture library predates the typed outputs.parts shape: its
            // manifests declare the parts directly under outputs.html, which this build's
            // reader does not type. Those packages are printed from the host's own table,
            // held to their files by PackageManifestsTests; every package the product can
            // read is read from the package itself.
            var packageId = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(templateFolder)));
            return PackageManifests.Of(packageId);
        }
    }

    /// <summary>
    /// The sample verb: the version's own <c>sample.json</c> composed, printed and verified
    /// through the take's port, with no fixture. The verified PDF is kept in the output folder
    /// for the demo; the result JSON carries the same figures the take's check reads.
    /// </summary>
    private static async Task<int> RenderSampleAsync(string templateFolder, string outFolder, string? stem)
    {
        Directory.CreateDirectory(outFolder);
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(templateFolder)));
        TemplateManifest manifest;
        using (var stream = File.OpenRead(Path.Combine(templateFolder, "template.yaml")))
        {
            var read = new TemplateManifestReader().Read(stream);
            if (!read.Succeeded || read.Manifest is null)
            {
                throw new InvalidDataException(read.Reason ?? $"{templateFolder} holds no readable template.yaml.");
            }

            manifest = read.Manifest;
        }

        var templateId = string.IsNullOrEmpty(manifest.Id) ? folderName : manifest.Id;
        var version = string.IsNullOrEmpty(manifest.Version) ? "0.0.0" : manifest.Version;
        var name = stem ?? folderName;
        var sample = await File.ReadAllBytesAsync(Path.Combine(templateFolder, "sample.json")).ConfigureAwait(false);

        await using var session = new BrowserSession();
        var printer = new ChromiumPdfPrinter(session, TimeProvider.System);
        using var metrics = new SampleRenderMetrics(MeterIdentity.ForHost("reportservice-rendering-host"));
        var renderer = new PdfSampleRenderer(
            new BlockComposer(TimeProvider.System),
            printer,
            new BlockVerifier(),
            TemplateEngineAdapters.Registry(),
            new PreparedTemplateCache(),
            metrics,
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new SampleRenderOptions()),
            scratchRoot: Path.Combine(Path.GetTempPath(), "report-service-host-samples"));

        var attempt = await renderer
            .RenderAsync(
                new SampleRequest(
                    templateId,
                    version,
                    manifest,
                    new FolderTemplatePackageFiles(templateFolder),
                    sample,
                    OutputDirectory: outFolder),
                CancellationToken.None)
            .ConfigureAwait(false);

        var pdf = templateId + "-" + version + ".pdf";
        var engine = manifest.Engine is { } declared ? TemplateEngineNames.ToManifestName(declared) : "undeclared";
        var result = attempt switch
        {
            SampleAttempt.Rendered rendered => new SampleResult(
                templateId, version, engine, SampleOutcomes.Rendered, pdf, rendered.Facts.HtmlSha256,
                rendered.Facts.Pages, rendered.Facts.HtmlBytes, rendered.Facts.PdfBytes, rendered.Facts.Words,
                rendered.Facts.ComposeMs, rendered.Facts.PrintMs, rendered.Facts.VerifyMs,
                null, null, null),
            SampleAttempt.Failed failed => new SampleResult(
                templateId, version, engine, SampleOutcomes.Refused, null, null,
                0, 0, 0, 0, 0, 0, 0,
                failed.Failure.Kind.ToString(), failed.Failure.Part, failed.Failure.Reason),
            SampleAttempt.Unavailable unavailable => new SampleResult(
                templateId, version, engine, SampleOutcomes.Unavailable, null, null,
                0, 0, 0, 0, 0, 0, 0,
                unavailable.Cause.ToString(), unavailable.Part, null),
            _ => throw new InvalidDataException("the sample renderer answered a state this host does not know"),
        };

        await WriteSampleResultAsync(Path.Combine(outFolder, name + ".json"), result).ConfigureAwait(false);
        Console.WriteLine($"{result.Outcome} {name}" + (result.Pdf is null ? string.Empty : $": {result.Pdf}"));
        return attempt is SampleAttempt.Unavailable ? ExitFailed : ExitOk;
    }

    private static async Task WriteSampleResultAsync(string path, SampleResult result) =>
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(result, SampleResultContext.Default.SampleResult) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);

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
/// <param name="Pdf">The PDF file the print wrote, or null when the block was refused before printing.</param>
/// <param name="Sha256">Its SHA-256, lower-case hexadecimal, or null when nothing was printed.</param>
/// <param name="HtmlSha256">The SHA-256 of the composed HTML the print was laid out from, lower-case hexadecimal.</param>
/// <param name="SizeBytes">Its size in bytes.</param>
/// <param name="AbortedRequests">How many requests of the document the route policy aborted.</param>
/// <param name="ConsoleErrors">How many console errors the page reported.</param>
/// <param name="DurationMs">How long the print took, in milliseconds.</param>
/// <param name="VerificationPassed">What the product's verification said about the document.</param>
/// <param name="VerificationReason">The reason its verification refused it with, or null.</param>
/// <param name="VerificationPage">The page its verification stopped on, or null.</param>
/// <param name="VerificationUnit">The unit its verification stopped at, or null.</param>
/// <param name="VerificationOffset">The offset within the page its verification stopped at, or null.</param>
/// <param name="VerificationField">The manifest path of a page-setting refusal, or null.</param>
/// <param name="VerificationMs">How long reading the document and aligning it took, in milliseconds, or null when the verification did not run.</param>
/// <param name="Pages">How many pages the verification read.</param>
/// <param name="Words">How many words the verification read.</param>
internal sealed record RenderResult(
    string Fixture,
    string Template,
    string? Mutation,
    string? Pdf,
    string? Sha256,
    string? HtmlSha256,
    long SizeBytes,
    int AbortedRequests,
    int ConsoleErrors,
    long DurationMs,
    bool VerificationPassed,
    string? VerificationReason,
    int? VerificationPage,
    int? VerificationUnit,
    int? VerificationOffset,
    string? VerificationField,
    long? VerificationMs,
    int Pages,
    int Words);

/// <summary>The serializer context of the result file, generated at build time.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RenderResult))]
internal sealed partial class RenderResultContext : JsonSerializerContext;

/// <summary>
/// What one sample render produced: the version, the PDF the demo keeps, and the figures
/// the take's check reads - or the failure that refused the version, with the file and the
/// reason an author acts on.
/// </summary>
/// <param name="Template">The template's identifier.</param>
/// <param name="Version">The version the package declares.</param>
/// <param name="Engine">The engine the version declares, as a manifest names it.</param>
/// <param name="Outcome">One of <see cref="SampleOutcomes"/>.</param>
/// <param name="Pdf">The PDF the demo keeps, or null when nothing was printed.</param>
/// <param name="HtmlSha256">The SHA-256 of the composed HTML the sample's page was laid out from, lower-case hexadecimal; the demo's identity of the three engines' report.</param>
/// <param name="Pages">How many pages the sample holds.</param>
/// <param name="HtmlBytes">The size of the composed HTML.</param>
/// <param name="PdfBytes">The size of the printed document.</param>
/// <param name="Words">How many words the printed document holds.</param>
/// <param name="ComposeMs">How long composing took.</param>
/// <param name="PrintMs">How long printing took.</param>
/// <param name="VerifyMs">How long verifying took.</param>
/// <param name="FailureKind">The class of a refusal, or null.</param>
/// <param name="FailurePart">The package-relative file a refusal names, or null.</param>
/// <param name="FailureReason">The verification reason a refusal names, or null.</param>
internal sealed record SampleResult(
    string Template,
    string Version,
    string Engine,
    string Outcome,
    string? Pdf,
    string? HtmlSha256,
    int Pages,
    long HtmlBytes,
    long PdfBytes,
    int Words,
    double ComposeMs,
    double PrintMs,
    double VerifyMs,
    string? FailureKind,
    string? FailurePart,
    string? FailureReason);

/// <summary>The three outcomes the sample verb writes.</summary>
internal static class SampleOutcomes
{
    /// <summary>The sample rendered and passed every check.</summary>
    public const string Rendered = "rendered";

    /// <summary>The version is refused for a deterministic property of its sample.</summary>
    public const string Refused = "refused";

    /// <summary>The render could not run or did not finish; nothing is said about the version.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>The serializer context of the sample verb's result file, generated at build time.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SampleResult))]
internal sealed partial class SampleResultContext : JsonSerializerContext;

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
        "BoundCharDropped",
        "HeaderAnchorDropped",
        "NC-F",
        "NC-L",
        "NC-R",
        "NC-W",
        "PF-MARGINS",
        "PF-OVERLAP",
        "PF-SIZE",
        InvariantCulturePresentation.ControlName,
        "RowDuplicated",
        "RowRemoved",
        "RowWithoutText",
    ];

    /// <summary>Whether the control acts on the printed page rather than on the composition.</summary>
    /// <param name="name">The control's name.</param>
    public static bool IsHtmlLevel(string name) => name is "BoundCharDropped" or "NC-W" or "NC-L" or "NC-R" or "NC-F" or "PF-SIZE" or "PF-OVERLAP" or "PF-MARGINS";

    /// <summary>Whether the control links a package stylesheet before the page rule.</summary>
    /// <param name="name">The control's name.</param>
    public static bool AppendsStylesheet(string name) => name is "PF-SIZE" or "PF-OVERLAP" or "PF-MARGINS";

    /// <summary>The stylesheet link the page-frame control adds before the page rule.</summary>
    /// <param name="name">The control's name.</param>
    public static string StylesheetLink(string name) => name switch
    {
        "PF-SIZE" => "<link rel=\"stylesheet\" href=\"https://report.invalid/controls/wrong-size.css\">",
        "PF-OVERLAP" => "<link rel=\"stylesheet\" href=\"https://report.invalid/controls/header-overlap.css\">",
        "PF-MARGINS" => "<link rel=\"stylesheet\" href=\"https://report.invalid/controls/own-margins.css\">",
        _ => throw new InvalidDataException($"The host knows no stylesheet control '{name}'."),
    };

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

        if (name == "BoundCharDropped" && rows.Count < 2)
        {
            throw new InvalidDataException("The control 'BoundCharDropped' needs a block with at least two records.");
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
            // C-2/C-4: the bound record's printed value loses one character after extraction.
            "BoundCharDropped" => Replace(block, rows[1], ChangeLongestText(block.TextOf(rows[1]), text => text[..^1])),
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
