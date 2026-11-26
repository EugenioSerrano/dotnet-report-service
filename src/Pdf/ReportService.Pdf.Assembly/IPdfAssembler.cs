namespace ReportService.Pdf.Assembly;

/// <summary>
/// The product's seam for assembling the rendered parts of one output into a
/// single globally numbered document. The seam is storage-agnostic and free of
/// the writing library's types: the caller hands ordered files and receives one
/// saved file, so the worker that owns the store never learns how pages are
/// imported, stamped or staged.
/// </summary>
public interface IPdfAssembler
{
    /// <summary>Assembles the ordered parts into one numbered document.</summary>
    /// <param name="request">The parts, the contract and where to write.</param>
    /// <param name="cancellationToken">The assembly's cancellation token.</param>
    /// <returns>The saved document and its measurements.</returns>
    Task<AssemblyResult> AssembleAsync(AssemblyRequest request, CancellationToken cancellationToken);
}
