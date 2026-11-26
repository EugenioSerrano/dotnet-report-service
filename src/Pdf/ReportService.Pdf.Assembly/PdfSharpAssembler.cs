using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ReportService.Pdf.Assembly;

/// <summary>An assembly that would produce a document over one of the ceiling's bounds.</summary>
public sealed class AssemblyLimitExceededException : Exception
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="limit">The bound that was passed: <c>document-pages</c> or <c>document-bytes</c>.</param>
    /// <param name="limitValue">The bound's value.</param>
    /// <param name="reachedValue">The value the assembly would reach.</param>
    public AssemblyLimitExceededException(string limit, long limitValue, long reachedValue)
        : base($"the assembly would pass {limit}: limit {limitValue}, reached {reachedValue}")
    {
        Limit = limit;
        LimitValue = limitValue;
        ReachedValue = reachedValue;
    }

    /// <summary>The bound that was passed.</summary>
    public string Limit { get; }

    /// <summary>The bound's value.</summary>
    public long LimitValue { get; }

    /// <summary>The value the assembly would reach.</summary>
    public long ReachedValue { get; }
}

/// <summary>An assembly whose inputs cannot be used as they were committed.</summary>
public sealed class AssemblyInputException : Exception
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="reason">The fixed reason, carrying no data value and no path.</param>
    public AssemblyInputException(string reason)
        : base(reason) => Reason = reason;

    /// <summary>The fixed reason.</summary>
    public string Reason { get; }
}

/// <summary>
/// Assembles the ordered parts of one output into one document, in manifest order,
/// in the staged shape the architecture fixes: batches of consecutive parts are
/// imported, every page is stamped with its global number, and each batch is
/// saved to temporary disk before the batch files are imported into the final
/// document. The stamp is drawn with the packaged font the contract names; the
/// staging files live in the caller's quota-checked folder and are removed on
/// success, failure and cancellation alike.
/// </summary>
public sealed class PdfSharpAssembler : IPdfAssembler
{
    private static readonly object ResolverGate = new();

    /// <inheritdoc />
    public async Task<AssemblyResult> AssembleAsync(AssemblyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parts);
        ArgumentNullException.ThrowIfNull(request.Numbering);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TemporaryRoot);
        if (request.Parts.Count == 0)
        {
            throw new AssemblyInputException("the output has no parts to assemble");
        }

        var metrics = request.Metrics ?? new AssemblyMetrics();
        metrics.Begin(request.Parts, request.Numbering);
        RefuseOverCeiling(request.Parts);
        EnsureResolver();
        var face = PackagedFontResolver.Instance.Register(request.Numbering.Font);
        var staging = Path.Combine(request.TemporaryRoot, "staging");
        Directory.CreateDirectory(staging);
        try
        {
            var total = request.Parts.Sum(part => part.PageCount);
            var batchSize = Math.Clamp(request.BatchSize, 1, AssemblyLimits.BatchSize);
            var batches = await StageAsync(request, face, staging, batchSize, metrics, cancellationToken)
                .ConfigureAwait(false);
            var pages = await MergeAsync(batches, request.OutputPath, metrics, cancellationToken)
                .ConfigureAwait(false);
            if (pages != total)
            {
                throw new AssemblyInputException("the assembled page count does not equal the parts' pages");
            }

            metrics.RecordPages(pages);
            metrics.RecordOutput(new FileInfo(request.OutputPath).Length);
            return new AssemblyResult(request.OutputPath, pages, metrics);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Refuses a part set that could only produce a document over the ceiling.</summary>
    /// <param name="parts">The parts.</param>
    private static void RefuseOverCeiling(IReadOnlyList<AssemblyPart> parts)
    {
        var pages = parts.Sum(part => (long)part.PageCount);
        if (pages > AssemblyLimits.MaximumPages)
        {
            throw new AssemblyLimitExceededException("document-pages", AssemblyLimits.MaximumPages, pages);
        }

        var bytes = parts.Sum(part => part.SizeBytes);
        if (bytes > AssemblyLimits.MaximumPartBytes)
        {
            throw new AssemblyLimitExceededException("document-bytes", AssemblyLimits.MaximumPartBytes, bytes);
        }
    }

    private async Task<List<string>> StageAsync(
        AssemblyRequest request,
        string face,
        string staging,
        int batchSize,
        AssemblyMetrics metrics,
        CancellationToken cancellationToken)
    {
        var batches = new List<string>();
        var pages = 0;
        await metrics.MeasureAsync(AssemblyStage.Batches, async () =>
        {
            var font = new XFont(face, request.Numbering.Font.SizePt, XFontStyleEx.Regular,
                new XPdfFontOptions(PdfFontEncoding.Unicode));
            for (var start = 0; start < request.Parts.Count; start += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchPath = Path.Combine(staging, $"batch-{batches.Count:0000}.pdf");
                using (var batch = new PdfDocument())
                {
                    for (var index = start; index < Math.Min(start + batchSize, request.Parts.Count); index++)
                    {
                        var part = request.Parts[index];
                        using var source = PdfReader.Open(part.Path, PdfDocumentOpenMode.Import);
                        if (source.PageCount != part.PageCount)
                        {
                            throw new AssemblyInputException("a part's page count does not match its commit");
                        }

                        foreach (var sourcePage in source.Pages)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var page = batch.AddPage(sourcePage);
                            pages++;
                            using var graphics = XGraphics.FromPdfPage(page);
                            var area = request.Numbering.Area;
                            graphics.DrawString(
                                request.Numbering.TextFor(pages, TotalOf(request)),
                                font,
                                XBrushes.Black,
                                new XRect(area.LeftPt, area.TopPt, area.WidthPt, area.HeightPt),
                                XStringFormats.TopLeft);
                        }
                    }

                    batch.Save(batchPath);
                }

                batches.Add(batchPath);
                metrics.RecordBatch(TemporaryBytes(staging));
                await Task.Yield();
            }
        }).ConfigureAwait(false);
        return batches;
    }

    private static int TotalOf(AssemblyRequest request) =>
        request.Parts.Sum(part => part.PageCount);

    private async Task<int> MergeAsync(
        IReadOnlyList<string> batches,
        string outputPath,
        AssemblyMetrics metrics,
        CancellationToken cancellationToken)
    {
        var pages = 0;
        await metrics.MeasureAsync(AssemblyStage.FinalMerge, () =>
        {
            using var final = new PdfDocument();
            foreach (var batchPath in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var batch = PdfReader.Open(batchPath, PdfDocumentOpenMode.Import);
                foreach (var page in batch.Pages)
                {
                    final.AddPage(page);
                    pages++;
                }
            }

            final.Save(outputPath);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        return pages;
    }

    private static void EnsureResolver()
    {
        lock (ResolverGate)
        {
            if (ReferenceEquals(GlobalFontSettings.FontResolver, PackagedFontResolver.Instance))
            {
                return;
            }

            try
            {
                GlobalFontSettings.FontResolver = PackagedFontResolver.Instance;
            }
            catch (InvalidOperationException exception)
            {
                throw new AssemblyInputException(
                    "another font resolver was installed before the assembly: " + exception.GetType().Name);
            }
        }
    }

    private static long TemporaryBytes(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A staging file the process cannot remove is left to the retention sweep.
        }
    }
}
