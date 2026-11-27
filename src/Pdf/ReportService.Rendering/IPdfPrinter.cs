using ReportService.Rendering.Layout;
using ReportService.Templates;

namespace ReportService.Rendering;

/// <summary>
/// The one way a composed document becomes a file: the product prints it with the
/// browser, and a test that checks the renderer's own steps is given a printer that
/// writes the document it was told to. Nothing in production implements it but
/// <see cref="ChromiumPdfPrinter"/>.
/// </summary>
public interface IPdfPrinter
{
    /// <summary>Prints <paramref name="request"/>.</summary>
    /// <param name="request">The document, its page rule, the mode and the destination.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<PrintOutcome> PrintAsync(PdfPrintRequest request, CancellationToken cancellationToken = default);

    /// <summary>Prints <paramref name="document"/> on the native path.</summary>
    /// <param name="document">The document to serve: the composed document followed by the page rule.</param>
    /// <param name="pageRule">The page rule the document carries, which the served policy allows by its hash.</param>
    /// <param name="files">The files of the taken package.</param>
    /// <param name="pdfPath">The file the document is printed into.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<PrintOutcome> PrintAsync(
        string document,
        PageRule pageRule,
        ITemplatePackageFiles files,
        string pdfPath,
        CancellationToken cancellationToken = default);
}
