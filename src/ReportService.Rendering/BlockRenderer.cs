using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.Rendering;

/// <summary>What one block's render is asked to do: compose, check, print and verify.</summary>
/// <param name="Manifest">The manifest of the pinned package, as the renderer reads it.</param>
/// <param name="Composition">The composition: the prepared package, the block, the header, the records and the fixed render values.</param>
/// <param name="Files">The files of the taken package.</param>
/// <param name="Partition">The job's partition record.</param>
/// <param name="Index">The index page's entry for this block.</param>
/// <param name="Block">The block document, read back.</param>
/// <param name="BlockIndex">The block's position in the partition.</param>
/// <param name="IsFirstBlock">Whether this is the first block of the job.</param>
/// <param name="WorkDirectory">The directory the printed file is written into.</param>
public sealed record BlockRenderRequest(
    RenderManifest Manifest,
    CompositionRequest Composition,
    ITemplatePackageFiles Files,
    PartitionFacts Partition,
    IndexFacts Index,
    BlockFacts Block,
    int BlockIndex,
    bool IsFirstBlock,
    string WorkDirectory);

/// <summary>What one block's render produced, and what the verification said about it.</summary>
/// <param name="PdfPath">The printed file, or <see langword="null"/> when the block was refused before printing.</param>
/// <param name="SizeBytes">The size of the printed file.</param>
/// <param name="Sha256">The SHA-256 of the printed file, lower-case hexadecimal.</param>
/// <param name="Print">What the print reported: aborted requests, console errors and its duration.</param>
/// <param name="Verification">The verification's verdict, with its reason and timings.</param>
public sealed record BlockRenderOutcome(
    string? PdfPath,
    long SizeBytes,
    string? Sha256,
    PrintOutcome? Print,
    VerificationResult Verification)
{
    /// <summary>Whether the block may be committed and published.</summary>
    public bool Passed => Verification.Passed && PdfPath is not null;

    /// <summary>How long composing the block took, in milliseconds; zero when it was refused before composing.</summary>
    public double ComposeMs { get; init; }
}

/// <summary>The product's one way to render a block: compose, check, print and verify it.</summary>
public interface IBlockRenderer
{
    /// <summary>Renders <paramref name="request"/>, or reports the reason it was refused.</summary>
    /// <param name="request">The block, its facts and the package to compose it with.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<BlockRenderOutcome> RenderAsync(BlockRenderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Renders one block: it refuses a package that declares page subtotals, refuses a
/// package that declares no field it prints as it is, composes the block, extracts
/// the text units of the composition, runs the checks that need no page - a failure
/// there ends the render with nothing printed - prints the document with the
/// product printer, and verifies what was printed against the composition and
/// against the block's own records. The printed file is returned only when every
/// check passed; a refused block leaves no file behind.
/// </summary>
public sealed class BlockRenderer(
    BlockComposer composer,
    IPdfPrinter printer,
    BlockVerifier verifier) : IBlockRenderer
{
    /// <summary>The product's one activity source: the phases of a render are its spans.</summary>
    private const string ActivitySourceName = "ReportService";

    private static readonly System.Diagnostics.ActivitySource Source = new(ActivitySourceName);
    /// <summary>Renders <paramref name="request"/>.</summary>
    /// <param name="request">The block, its facts and the package to compose it with.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    public Task<BlockRenderOutcome> RenderAsync(BlockRenderRequest request, CancellationToken cancellationToken = default) =>
        RenderAsync(request, blockMutation: null, htmlMutation: null, cancellationToken);

    /// <summary>
    /// Renders <paramref name="request"/>, applying the test-only controls a known-bad
    /// document needs: the first before the expected units are extracted, so the
    /// reference is wrong too, and the second after they are extracted, so only the
    /// printed document is wrong. Nothing in production passes either of them.
    /// </summary>
    internal async Task<BlockRenderOutcome> RenderAsync(
        BlockRenderRequest request,
        IComposedBlockMutation? blockMutation,
        IComposedHtmlMutation? htmlMutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var blockIndex = request.BlockIndex;

        if (request.Manifest.DeclaresPageTotals)
        {
            return Refused(blockIndex, VerificationReasons.PageAccounting);
        }

        if (request.Manifest.Pdf.Verification is null)
        {
            return Refused(blockIndex, VerificationReasons.AnchorsUndeclared);
        }

        var composed = Compose(request, cancellationToken, out var composeMs);
        if (blockMutation is not null)
        {
            composed = blockMutation.Apply(composed);
        }

        var text = new TextUnitExtractor().Extract(composed);
        var source = new SourceCheckInput(
            request.Partition,
            request.Index,
            request.Block,
            composed,
            text,
            request.Manifest.Pdf.Verification,
            request.IsFirstBlock);

        SourceCheckOutcome sourceChecks;
        long sourceChecksMs;
        using (var sourceSpan = Source.StartActivity("verify"))
        {
            sourceSpan?.SetTag("block.index", blockIndex);
            sourceSpan?.SetTag("phase", "source-checks");
            sourceChecks = verifier.CheckSource(source, out sourceChecksMs);
            sourceSpan?.SetTag("verification.passed", sourceChecks.Passed);
        }

        if (!sourceChecks.Passed)
        {
            return new BlockRenderOutcome(
                PdfPath: null,
                SizeBytes: 0,
                Sha256: null,
                Print: null,
                new VerificationResult(
                    Passed: false,
                    Reason: sourceChecks.Reason,
                    BlockIndex: blockIndex,
                    RecordOrdinal: sourceChecks.RecordOrdinal,
                    AnchorPath: sourceChecks.AnchorPath,
                    Page: null,
                    UnitIndex: null,
                    Offset: null,
                    Pages: 0,
                    Words: 0,
                    ReadAndAlignMs: 0,
                    SourceChecksMs: sourceChecksMs))
            {
                ComposeMs = composeMs,
            };
        }

        Directory.CreateDirectory(request.WorkDirectory);
        var pdfPath = Path.Combine(request.WorkDirectory, "block-" + blockIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".pdf");
        var html = htmlMutation is null ? composed.Text : htmlMutation.Apply(composed.Text);

        PrintOutcome print;
        using (var printSpan = Source.StartActivity("print"))
        {
            printSpan?.SetTag("block.index", blockIndex);
            print = await printer.PrintAsync(html, request.Files, pdfPath, cancellationToken).ConfigureAwait(false);
            printSpan?.SetTag("print.aborted_requests", print.AbortedRequests);
            printSpan?.SetTag("print.console_errors", print.ConsoleErrors);
        }

        VerificationResult verification;
        using (var verifySpan = Source.StartActivity("verify"))
        {
            verifySpan?.SetTag("block.index", blockIndex);
            verification = verifier.Verify(new PrintedCheckInput(
                pdfPath,
                blockIndex,
                PlacementConvention.FromMillimetres(request.Manifest.Pdf.MarginMm),
                composed,
                text,
                sourceChecksMs));
            verifySpan?.SetTag("verification.passed", verification.Passed);
            verifySpan?.SetTag("verification.pages", verification.Pages);
            if (verification.Reason is { } reason)
            {
                verifySpan?.SetTag("verification.reason", reason);
            }
        }

        if (!verification.Passed)
        {
            Delete(pdfPath);
            return new BlockRenderOutcome(null, 0, null, print, verification)
            {
                ComposeMs = composeMs,
            };
        }

        var bytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken).ConfigureAwait(false);
        return new BlockRenderOutcome(
            pdfPath,
            bytes.LongLength,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            print,
            verification)
        {
            ComposeMs = composeMs,
        };
    }

    private ComposedBlock Compose(BlockRenderRequest request, CancellationToken cancellationToken, out double composeMs)
    {
        using var span = Source.StartActivity("compose");
        span?.SetTag("block.index", request.BlockIndex);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return composer.Compose(request.Composition, cancellationToken);
        }
        finally
        {
            composeMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            span?.SetTag("compose.duration_ms", composeMs);
        }
    }

    private static BlockRenderOutcome Refused(int blockIndex, string reason) =>
        new(
            PdfPath: null,
            SizeBytes: 0,
            Sha256: null,
            Print: null,
            new VerificationResult(
                Passed: false,
                Reason: reason,
                BlockIndex: blockIndex,
                RecordOrdinal: null,
                AnchorPath: null,
                Page: null,
                UnitIndex: null,
                Offset: null,
                Pages: 0,
                Words: 0,
                ReadAndAlignMs: 0,
                SourceChecksMs: 0));

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A file that cannot be removed costs disk before it costs correctness.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
