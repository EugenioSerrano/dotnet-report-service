namespace ReportService.Pdf.Assembly;

/// <summary>
/// One rendered part of an output, in the order the block manifest fixes: the
/// temporary file the merge worker copied it to, the page count its commit
/// recorded and the size the commit recorded. The assembly never decides the
/// order itself; the caller hands the parts in manifest order.
/// </summary>
/// <param name="PartIndex">The part's position in manifest order, from zero.</param>
/// <param name="Path">The temporary file the part was written to.</param>
/// <param name="PageCount">How many pages the part's document holds, as recorded at commit.</param>
/// <param name="SizeBytes">How many bytes the part's document holds, as recorded at commit.</param>
public sealed record AssemblyPart(int PartIndex, string Path, int PageCount, long SizeBytes);

/// <summary>What one assembly is asked to do: the ordered parts, the numbering contract and where to write.</summary>
/// <param name="Parts">The parts, in manifest order.</param>
/// <param name="Numbering">The template's numbering contract; absent only for a one-part assembly that draws a page code and no number.</param>
/// <param name="OutputPath">The file the assembled document is saved to; the caller owns it.</param>
/// <param name="TemporaryRoot">The quota-checked folder the staging files live under.</param>
/// <param name="BatchSize">How many consecutive parts one staging batch imports.</param>
/// <param name="Metrics">The measurements the assembly records, when the caller supplies one.</param>
/// <param name="OnBatch">Called after each staging batch was saved, with the number of batches written so far.</param>
/// <param name="PageCode">The per-page code drawn in the same pass as the number, when the template declares one.</param>
/// <param name="MaximumPages">The deployment's configured page capacity the parts are re-checked against before anything is read; no check when absent.</param>
/// <param name="MaximumPartBytes">The deployment's configured parts-bytes capacity the parts are re-checked against before anything is read; no check when absent.</param>
public sealed record AssemblyRequest(
    IReadOnlyList<AssemblyPart> Parts,
    PageNumbering? Numbering,
    string OutputPath,
    string TemporaryRoot,
    int BatchSize = AssemblyLimits.BatchSize,
    AssemblyMetrics? Metrics = null,
    Func<int, ValueTask>? OnBatch = null,
    PageCodes.AssemblyPageCode? PageCode = null,
    int? MaximumPages = null,
    long? MaximumPartBytes = null)
{
    /// <summary>
    /// Whether the request's shape is one the assembly accepts: a numbering contract,
    /// or no numbering with exactly one part and a page code.
    /// </summary>
    public bool IsWellFormed => Numbering is not null || (Parts?.Count == 1 && PageCode is not null);
}
