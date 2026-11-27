using System.Security.Cryptography;
using ReportService.Pdf.Assembly;
using ReportService.Pdf.Assembly.PageCodes;
using ReportService.Pdf.Verification;
using ReportService.Pdf.Verification.PageCodes;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;

namespace ReportService.TemplateChecks.Sample;

/// <summary>
/// The take's proof that the printed sample carries the declared per-page code: no
/// page prints template text inside the code's area, the source yields a value on
/// every page, the generator draws allowed content within the stored bound for each,
/// and a one-part assembly of the sample passes the assembly verification without a
/// numbering contract. It runs on the populated sample only: a report with no
/// records has no record text for a pattern to match.
/// </summary>
public static class PageCodeSample
{
    /// <summary>The reason words a sample failure carries.</summary>
    public static class Reasons
    {
        /// <summary>A page prints template text inside the code's area.</summary>
        public const string TextInCodeArea = "text-in-code-area";

        /// <summary>A page's source yields no value.</summary>
        public const string NoValue = "no-value";

        /// <summary>The generator refuses a page's value, or is not registered.</summary>
        public const string GeneratorRefused = "generator-refused";

        /// <summary>The generator returns content the product does not accept.</summary>
        public const string ContentNotAllowed = "content-not-allowed";

        /// <summary>A page's code is larger than the stored bound.</summary>
        public const string TooLarge = "too-large";

        /// <summary>The coded sample does not pass the assembly verification.</summary>
        public const string Verification = "verification";
    }

    /// <summary>
    /// The sample's answer once the code check ran: the refusal, after the printed
    /// sample is discarded, or the rendered attempt the caller builds.
    /// </summary>
    /// <param name="failure">The code check's failure, when it failed.</param>
    /// <param name="discard">Removes the printed sample.</param>
    /// <param name="rendered">Builds the rendered attempt.</param>
    /// <returns>The attempt.</returns>
    public static SampleAttempt Outcome(SampleFailure? failure, Action discard, Func<SampleAttempt> rendered)
    {
        ArgumentNullException.ThrowIfNull(discard);
        ArgumentNullException.ThrowIfNull(rendered);
        if (failure is null)
        {
            return rendered();
        }

        discard();
        return new SampleAttempt.Failed(failure);
    }

    /// <summary>
    /// The renderer's step after a sample printed and verified: nothing for the
    /// sample with zero records or a version that declares no code (no pages
    /// counted), else <see cref="CheckAsync"/> with the deployment's generators -
    /// none registered when the process registered none.
    /// </summary>
    /// <param name="request">The sample render request.</param>
    /// <param name="registry">The process's generators, when it registered them.</param>
    /// <param name="pdfPath">The printed, verified sample.</param>
    /// <param name="frame">The template's page frame.</param>
    /// <param name="work">A temporary directory the check may write in.</param>
    /// <param name="cancellationToken">The take's cancellation.</param>
    /// <returns>The failure, or the pages that carry the code (<see langword="null"/> when none is declared or checked).</returns>
    public static async Task<(SampleFailure? Failure, int? CodePages)> AfterPrintAsync(
        SampleRequest request,
        PageCodeGeneratorRegistry? registry,
        string pdfPath,
        PageFrame frame,
        string work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EmptySample || request.Manifest.Outputs?.Pdf?.PageCode is null)
        {
            return (null, null);
        }

        var (failure, pages) = await CheckAsync(
            request.Manifest,
            registry ?? new PageCodeGeneratorRegistry([]),
            pdfPath,
            frame,
            work,
            cancellationToken).ConfigureAwait(false);
        return (failure, failure is null ? pages : null);
    }

    /// <summary>Checks one printed sample.</summary>
    /// <param name="manifest">The version's manifest.</param>
    /// <param name="registry">The deployment's generators.</param>
    /// <param name="pdfPath">The printed, verified sample.</param>
    /// <param name="frame">The template's page frame.</param>
    /// <param name="work">A temporary directory the check may write in; the caller removes it.</param>
    /// <param name="cancellationToken">The take's cancellation.</param>
    /// <returns>The failure, or the number of pages that carry the code.</returns>
    public static async Task<(SampleFailure? Failure, int CodePages)> CheckAsync(
        TemplateManifest manifest,
        PageCodeGeneratorRegistry registry,
        string pdfPath,
        PageFrame frame,
        string work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(work);

        var pageCode = manifest.Outputs?.Pdf?.PageCode ?? throw new ArgumentException("the manifest declares no page code", nameof(manifest));
        var declaration = PageCodeDeclaration.Create(PageCodeManifest.Raw(pageCode), new List<PageCodeProblem>());
        if (declaration is null || !registry.TryResolve(declaration.Generator, out var generator))
        {
            return (Failure(Reasons.GeneratorRefused, 1), 0);
        }

        var numbering = manifest.Outputs!.Pdf!.Numbering?.Area is { LeftMm: { } left, TopMm: { } top, WidthMm: { } width, HeightMm: { } height }
            ? new NumberingArea((double)left, (double)top, (double)width, (double)height)
            : null;
        var code = new AssemblyPageCode(
            declaration,
            generator,
            manifest.Id ?? string.Empty,
            manifest.Version ?? string.Empty,
            frame.Margins,
            numbering,
            AssemblyPageCode.PrintableBoxOf(frame),
            manifest.Outputs.Pdf.PageTotals is { Enabled: true, Footer: { LeftMm: { } fLeft, TopMm: { } fTop, WidthMm: { } fWidth, HeightMm: { } fHeight } }
                ? PageCodeText.Region.FromMillimetres((double)fLeft, (double)fTop, (double)fWidth, (double)fHeight)
                : null);

        var pages = PdfWordReader.Read(pdfPath, frame.Margins).Pages;
        for (var index = 0; index < pages.Count; index++)
        {
            var number = index + 1;
            if (PageCodeText.Inside(pages[index], code.CodeRegion).Count > 0)
            {
                return (Failure(Reasons.TextInCodeArea, number), 0);
            }

            var value = code.ValueFor(pages[index], number, pages.Count);
            if (value is null)
            {
                return (Failure(Reasons.NoValue, number), 0);
            }

            var generated = generator.Generator.Generate(value, declaration.Options, declaration.Area.Size);
            if (generated.Content is not { } content)
            {
                return (Failure(Reasons.GeneratorRefused, number), 0);
            }

            if (PageCodeContentValidator.Problem(content) is not null)
            {
                return (Failure(Reasons.ContentNotAllowed, number), 0);
            }

            if (PageCodeFormWriter.StoredSize(content, declaration.Area.Size) > PageCodeContentValidator.MaximumStoredBytes)
            {
                return (Failure(Reasons.TooLarge, number), 0);
            }
        }

        Directory.CreateDirectory(work);
        var output = Path.Combine(work, "sample-coded.pdf");
        var part = new AssemblyPart(0, pdfPath, pages.Count, new FileInfo(pdfPath).Length);
        try
        {
            await new PdfSharpAssembler()
                .AssembleAsync(new AssemblyRequest([part], null, output, work, PageCode: code), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AssemblyInputException)
        {
            return (Failure(Reasons.Verification, 1), 0);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(pdfPath, cancellationToken).ConfigureAwait(false)));
        var verdict = AssemblyVerifier.Verify(new AssemblyVerificationRequest(
            output,
            [new AssemblyPartExpectation(0, pdfPath, pages.Count, part.SizeBytes, hash)],
            frame,
            null,
            code));
        return verdict.Passed
            ? (null, pages.Count)
            : (Failure(Reasons.Verification + ":" + verdict.Reason, verdict.PageNumber ?? 1), 0);
    }

    private static SampleFailure Failure(string reason, int page) =>
        new(SampleFailureKind.PageCode, PackagePath.ManifestFileName, Reason: reason, PageNumber: page);
}
