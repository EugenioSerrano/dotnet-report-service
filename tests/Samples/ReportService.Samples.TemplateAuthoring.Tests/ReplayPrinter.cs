using ReportService.Rendering;
using ReportService.Rendering.Layout;
using ReportService.Templates;

namespace ReportService.Samples.TemplateAuthoring.Tests;

/// <summary>
/// The printer every hermetic case uses: it records the document it was handed and
/// copies the committed golden to the path the product renderer asked for, so the
/// renderer's own post-render verification reads the same printed page a live print
/// produced. A second mode throws <see cref="PrintTemporarilyFailedException"/>,
/// which is the product's "could not run" and never a refusal of the package.
/// </summary>
internal sealed class ReplayPrinter(string goldenPath, bool unavailable = false) : IPdfPrinter
{
    private readonly string _goldenPath = goldenPath;
    private readonly bool _unavailable = unavailable;

    /// <summary>How many prints were requested.</summary>
    public int Calls { get; private set; }

    /// <summary>The document the last print was handed, or <see langword="null"/> before any print.</summary>
    public string? Document { get; private set; }

    /// <inheritdoc />
    public Task<PrintOutcome> PrintAsync(PdfPrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Calls++;
        Document = request.Document;
        if (_unavailable)
        {
            throw new PrintTemporarilyFailedException("the replay printer was asked to report an unavailable render");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.PdfPath)!);
        File.Copy(_goldenPath, request.PdfPath, overwrite: true);
        return Task.FromResult(new PrintOutcome(request.PdfPath, 0, 0, TimeSpan.Zero));
    }

    /// <inheritdoc />
    public Task<PrintOutcome> PrintAsync(
        string document,
        PageRule pageRule,
        ITemplatePackageFiles files,
        string pdfPath,
        CancellationToken cancellationToken = default) =>
        PrintAsync(
            new PdfPrintRequest(document, pageRule, files, PdfPrintMode.Native, Accounting: null, pdfPath),
            cancellationToken);
}
