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
    /// <summary>Prints <paramref name="html"/> to <paramref name="pdfPath"/>.</summary>
    /// <param name="html">The composed document.</param>
    /// <param name="files">The files of the taken package.</param>
    /// <param name="pdfPath">The file the document is printed into.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task<PrintOutcome> PrintAsync(
        string html,
        ITemplatePackageFiles files,
        string pdfPath,
        CancellationToken cancellationToken = default);
}
