using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReportService.Pdf.Verification;
using ReportService.Rendering;
using ReportService.ServiceDefaults;
using ReportService.TemplateChecks;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Versioning;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Manifest;
using ReportService.Templates;
using ReportService.Workers.Pdf.Templates;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>What one scaffold is asked for: the engine, the identifier and where the package goes.</summary>
/// <param name="Engine">The engine's manifest name, read without regard to case: <c>scriban</c>, <c>handlebars</c> or <c>liquid</c>.</param>
/// <param name="TemplateId">The template identifier the manifest declares.</param>
/// <param name="OutputDirectory">The package directory the tool creates and writes.</param>
/// <param name="Version">The version the manifest declares.</param>
public sealed record ScaffoldRequest(
    string Engine,
    string TemplateId,
    string OutputDirectory,
    string Version = "1.0.0");

/// <summary>What one local render is asked for.</summary>
/// <param name="PackageDirectory">The package folder to check and render.</param>
/// <param name="OutputDirectory">Where the two files go, or <see langword="null"/> for <c>artifacts/template-preview/&lt;id&gt;/&lt;version&gt;</c>.</param>
public sealed record RenderRequest(string PackageDirectory, string? OutputDirectory = null);

/// <summary>
/// The local authoring loop around a template package. <see cref="Scaffold"/> writes a
/// complete package for one engine from the tool's embedded scaffold set and runs the
/// take's thirteen static checks on it. <see cref="RenderAsync"/> runs the same checks,
/// then renders the package's own <c>sample.json</c> through the product's one sample
/// renderer - the take's engine, composition, Chromium print and post-render
/// verification - and writes the composed HTML and the verified PDF.
/// <para>
/// The tool adds no rule, no order and no refusal wording: every decision is a product
/// type, and a refused package prints that check's own sentence. A render that cannot
/// run is never turned into a refusal of the package.
/// </para>
/// </summary>
/// <param name="clock">The clock the presenter reads, or <see langword="null"/> for the system clock.</param>
/// <param name="printer">A printer for a caller that runs without a browser, or <see langword="null"/> for the product's Chromium printer.</param>
public sealed class TemplateAuthoringTool(TimeProvider? clock = null, Func<IPdfPrinter>? printer = null)
{
    private const string TypedMemberSentence =
        "template.yaml: the manifest declares a member with a type this service does not read.";

    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly Func<IPdfPrinter>? _printer = printer;

    /// <summary>Writes a complete package for the requested engine and checks it with the take's checks.</summary>
    /// <param name="request">The engine, the identifier, the version and the target directory.</param>
    /// <param name="output">Where the result or the usage line is written.</param>
    /// <returns>0 for a scaffolded package the checks accept, 2 for a usage error, 3 for a tool defect.</returns>
    public int Scaffold(ScaffoldRequest request, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);

        if (!ScaffoldWriter.TryParseEngine(request.Engine, out var engine))
        {
            output.WriteLine(
                "error: the engine '" + request.Engine + "' is not one this tool scaffolds; it scaffolds " + ScaffoldWriter.EngineNames + ".");
            return 2;
        }

        if (!IsTemplateId(request.TemplateId))
        {
            output.WriteLine(
                "error: '" + request.TemplateId
                + "' is not a template identifier (lower-case a-z, 0-9, '.', '_' and '-', beginning and ending with a letter or a digit, at most 128 characters).");
            return 2;
        }

        if (!SemanticVersionSyntax.IsValid(request.Version))
        {
            output.WriteLine(
                "error: '" + request.Version
                + "' is not a semantic version (MAJOR.MINOR.PATCH with an optional pre-release and build metadata).");
            return 2;
        }

        if (TargetPath(request.OutputDirectory) is not { } target)
        {
            output.WriteLine("error: the output directory is not a usable path.");
            return 2;
        }

        if (File.Exists(target)
            || (Directory.Exists(target) && Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any()))
        {
            output.WriteLine("error: '" + target + "' exists and holds files; name a directory the tool may create.");
            return 2;
        }

        var name = TemplateEngineNames.ToManifestName(engine);
        var registry = TemplateEngineAdapters.Registry();
        if (!registry.TryGet(name, out var adapter))
        {
            output.WriteLine("internal-error: the deployment offers no " + name + " adapter.");
            return 3;
        }

        try
        {
            ScaffoldWriter.Write(target, request.TemplateId, request.Version, engine, adapter.Version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            output.WriteLine("error: the package could not be written (" + exception.GetType().Name + ").");
            return 3;
        }

        var read = ReadManifest(target);
        if (!read.Succeeded)
        {
            output.WriteLine(read.Reason);
            return 3;
        }

        if (read.Manifest is not { } manifest)
        {
            output.WriteLine(TypedMemberSentence);
            return 3;
        }

        var candidate = new TemplateCandidate(
            request.TemplateId,
            request.Version,
            manifest,
            read.CanonicalJson!,
            target,
            PackageDigest.Of(target));

        // A refusal here is a tool defect, not the author's: the scaffold set is the tool's own.
        var unmet = new DecidedAttemptRenderer(new SampleAttempt.Unavailable(SampleUnavailableCause.RenderCrash));
        var refusal = RunStaticChecksAsync(candidate, unmet, CancellationToken.None).GetAwaiter().GetResult();
        if (refusal is not null)
        {
            output.WriteLine(refusal.Code + ": " + refusal.Reason);
            return 3;
        }

        output.WriteLine("scaffolded " + name + " " + request.TemplateId + " " + request.Version + " at " + target + " checks=pass");
        return 0;
    }

    /// <summary>Runs the take's static checks on a package and renders its own sample locally.</summary>
    /// <param name="request">The package folder and, optionally, where the two files go.</param>
    /// <param name="output">Where the result or the refusal is written.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>0 for a rendered and verified sample, 1 for a refused package, 2 for a usage error, 3 when the render could not run.</returns>
    public async Task<int> RenderAsync(RenderRequest request, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);

        if (string.IsNullOrWhiteSpace(request.PackageDirectory))
        {
            output.WriteLine("error: a package folder is required.");
            return 2;
        }

        if (TargetPath(request.PackageDirectory) is not { } packageDirectory)
        {
            output.WriteLine("error: the package folder is not a usable path.");
            return 2;
        }

        if (!Directory.Exists(packageDirectory))
        {
            output.WriteLine("error: the package folder '" + packageDirectory + "' does not exist.");
            return 2;
        }

        var manifestPath = Path.Combine(packageDirectory, "template.yaml");
        if (!File.Exists(manifestPath))
        {
            output.WriteLine("error: '" + manifestPath + "' does not exist.");
            return 2;
        }

        var fixedClock = new FixedTimeProvider(FixedInstant);
        var scratchRoot = Path.Combine(
            Path.GetTempPath(),
            "report-service-template-authoring",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var scratchOutput = Path.Combine(scratchRoot, "output");
        BrowserSession? session = null;
        SampleRenderMetrics? metrics = null;
        try
        {
            var read = ReadManifest(packageDirectory);
            if (!read.Succeeded)
            {
                output.WriteLine(read.Reason);
                return 1;
            }

            if (read.Manifest is not { } manifest)
            {
                output.WriteLine(TypedMemberSentence);
                return 1;
            }

            if (manifest.Engine is not { } declaredEngine)
            {
                output.WriteLine("TEMPLATE_INVALID: template.yaml: the manifest declares no engine. Declare scriban, handlebars or liquid.");
                return 1;
            }

            var templateId = string.IsNullOrEmpty(manifest.Id) ? Path.GetFileName(packageDirectory) : manifest.Id;
            var version = string.IsNullOrEmpty(manifest.Version) ? "0.0.0" : manifest.Version;
            var files = new FolderTemplatePackageFiles(packageDirectory);
            var candidate = new TemplateCandidate(
                templateId,
                version,
                manifest,
                read.CanonicalJson!,
                packageDirectory,
                PackageDigest.Of(packageDirectory));

            var registry = TemplateEngineAdapters.Registry();
            IPdfPrinter pdfPrinter;
            if (_printer is { } supplied)
            {
                pdfPrinter = supplied();
            }
            else
            {
                session = new BrowserSession();
                pdfPrinter = new ChromiumPdfPrinter(session, fixedClock);
            }

            metrics = new SampleRenderMetrics(MeterIdentity.ForHost(TelemetryDefaults.MeterName));
            var renderer = new PdfSampleRenderer(
                new BlockComposer(fixedClock),
                pdfPrinter,
                new BlockVerifier(),
                registry,
                new PreparedTemplateCache(),
                metrics,
                fixedClock,
                Options.Create(new SampleRenderOptions()),
                scratchRoot);

            var unmet = await RunStaticChecksAsync(candidate, renderer, cancellationToken).ConfigureAwait(false);
            if (unmet is not null)
            {
                output.WriteLine(unmet.Code + ": " + unmet.Reason);
                return 1;
            }

            var engineName = TemplateEngineNames.ToManifestName(declaredEngine);
            if (!registry.TryGet(engineName, out var adapter))
            {
                output.WriteLine("UNSUPPORTED_CAPABILITY: reason=engine-unsupported field=engine declared=" + engineName);
                return 1;
            }

            var renderManifest = RenderManifestMapping.From(manifest, declaredEngine, adapter.Version);
            var decision = EngineResolution.Resolve(renderManifest, pinEngine: null, registry);
            if (decision.Adapter is not { } composition)
            {
                output.WriteLine(decision.Message);
                return 1;
            }

            var sampleBytes = await File.ReadAllBytesAsync(Path.Combine(packageDirectory, "sample.json"), cancellationToken)
                .ConfigureAwait(false);
            var attempt = await renderer
                .RenderAsync(
                    new SampleRequest(
                        templateId,
                        version,
                        manifest,
                        files,
                        sampleBytes,
                        candidate.SourceDescriptorSha256,
                        EmptySample: false,
                        OutputDirectory: scratchOutput),
                    cancellationToken)
                .ConfigureAwait(false);

            switch (attempt)
            {
                case SampleAttempt.Failed:
                    {
                        // The take's check builds the sentence over the already-decided attempt.
                        var check = await new SampleRenderCheck(new DecidedAttemptRenderer(attempt))
                            .CheckAsync(candidate, cancellationToken)
                            .ConfigureAwait(false);
                        output.WriteLine(check.Code + ": " + check.Reason);
                        return 1;
                    }

                case SampleAttempt.Unavailable unavailable:
                    output.WriteLine(
                        "render-unavailable: " + unavailable.Cause
                        + (unavailable.Part is { } part ? " part=" + part : string.Empty));
                    return 3;

                case SampleAttempt.Rendered rendered:
                    return await WritePreviewAsync(
                        manifest,
                        renderManifest,
                        composition,
                        files,
                        sampleBytes,
                        fixedClock,
                        templateId,
                        version,
                        engineName,
                        rendered.Facts,
                        scratchOutput,
                        request.OutputDirectory,
                        output,
                        cancellationToken).ConfigureAwait(false);

                default:
                    output.WriteLine("internal-error: the sample renderer answered a state this tool does not know.");
                    return 3;
            }
        }
        finally
        {
            Delete(scratchRoot);
            metrics?.Dispose();
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<int> WritePreviewAsync(
        TemplateManifest manifest,
        RenderManifest renderManifest,
        ITemplateEngineAdapter adapter,
        ITemplatePackageFiles files,
        ReadOnlyMemory<byte> sampleBytes,
        FixedTimeProvider clock,
        string templateId,
        string version,
        string engineName,
        SampleFacts facts,
        string scratchOutput,
        string? namedOutput,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var presentation = SettingsOf(manifest, clock.GetUtcNow());
        var prepared = adapter.Prepare(renderManifest, files);
        var partition = await SamplePartition.ReadAsync(sampleBytes, cancellationToken).ConfigureAwait(false);
        if (partition is not SampleRead.Partitioned partitioned)
        {
            output.WriteLine("internal-error: the sample could not be partitioned after the render accepted it.");
            return 3;
        }

        var block = await new BlockComposer(clock)
            .ComposeAsync(
                new CompositionRequest(
                    prepared,
                    partitioned.Sample.Block,
                    partitioned.Sample.Header,
                    partitioned.Sample.Records,
                    new CompositionRender(presentation.Culture.Name, presentation.TimeZone.Id, presentation.Clock),
                    manifest.Partitioning?.GroupKey),
                adapter,
                presentation,
                cancellationToken)
            .ConfigureAwait(false);

        var page = PdfPageSettingsRules.Resolve(renderManifest.PdfOutput).Settings;
        if (page is null)
        {
            output.WriteLine("internal-error: the page settings could not be read after the checks accepted them.");
            return 3;
        }

        var html = PageRule.From(page).AppendTo(block.Text);
        var renderedPdf = Path.Combine(scratchOutput, templateId + "-" + version + ".pdf");
        if (!File.Exists(renderedPdf))
        {
            output.WriteLine("internal-error: the verified PDF is not where the renderer keeps it.");
            return 3;
        }

        var outputDirectory = namedOutput is { Length: > 0 }
            ? Path.GetFullPath(namedOutput)
            : Path.Combine(
                Directory.GetCurrentDirectory(),
                "artifacts",
                "template-preview",
                templateId,
                version);
        Directory.CreateDirectory(outputDirectory);

        var htmlPath = Path.Combine(outputDirectory, templateId + "-" + version + ".html");
        var pdfPath = Path.Combine(outputDirectory, templateId + "-" + version + ".pdf");
        File.WriteAllText(htmlPath, html, Utf8NoBom);
        File.Copy(renderedPdf, pdfPath, overwrite: true);

        output.WriteLine(
            "rendered " + templateId + " " + version + ": engine=" + engineName
            + " pages=" + facts.Pages.ToString(CultureInfo.InvariantCulture)
            + " words=" + facts.Words.ToString(CultureInfo.InvariantCulture)
            + " html=" + htmlPath
            + " pdf=" + pdfPath);
        return 0;
    }

    /// <summary>
    /// The take's presentation rule for a sample: the package's own locale when it declares
    /// both a culture and a time zone, else the invariant culture and UTC at the fixed instant.
    /// </summary>
    private static PresentationSettings SettingsOf(TemplateManifest manifest, DateTimeOffset clock)
    {
        var locale = manifest.LocaleDefaults;
        if (locale is { Culture: { Length: > 0 } culture, TimeZone: { Length: > 0 } zone })
        {
            return PresentationSettingsResolver.Resolve(new PresentationValues(culture, zone, clock));
        }

        return new PresentationSettings(CultureInfo.InvariantCulture, TimeZoneInfo.Utc, clock.ToUniversalTime());
    }

    /// <summary>Runs the take's thirteen static checks over the candidate, in the take's own order.</summary>
    private async Task<TemplateCheckResult?> RunStaticChecksAsync(
        TemplateCandidate candidate,
        ISampleRenderer renderer,
        CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_clock);
        services.AddTemplateVersionChecks();
        services.AddSingleton(renderer);
        using var provider = services.BuildServiceProvider();

        var checks = provider
            .GetServices<ITemplateVersionCheck>()
            .Where(check => !IsSampleRender(check.Name));
        var result = await TemplateVersionChecks.RunAsync(checks, candidate, cancellationToken).ConfigureAwait(false);
        return result.Passed ? null : result;
    }

    /// <summary>Whether a check is one of the two renders the local loop does not run.</summary>
    private static bool IsSampleRender(string name) =>
        string.Equals(name, SampleRenderCheck.CheckName, StringComparison.Ordinal)
        || string.Equals(name, AtBoundSampleCheck.CheckName, StringComparison.Ordinal);

    private static ManifestReadResult ReadManifest(string packageDirectory)
    {
        using var stream = File.OpenRead(Path.Combine(packageDirectory, "template.yaml"));
        return new TemplateManifestReader().Read(stream);
    }

    private static string? TargetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The documented template identifier: lower-case letters and digits, '.' '_' and '-', ending with a letter or digit.</summary>
    private static bool IsTemplateId(string? id) =>
        id is { Length: > 0 and <= 128 }
        && IsIdEdge(id[0])
        && IsIdEdge(id[^1])
        && id.All(character => IsIdEdge(character) || character is '.' or '_' or '-');

    private static bool IsIdEdge(char character) => character is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover scratch tree costs disk before it costs correctness.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
