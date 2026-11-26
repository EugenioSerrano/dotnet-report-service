using ReportService.Templates;

namespace ReportService.Rendering.Layout;

/// <summary>Which print a block asks for: the native Chromium layout, or the page-accounting path.</summary>
public enum PdfPrintMode
{
    /// <summary>The native Chromium print layout: scripts disabled, one content security policy, the package's files from memory.</summary>
    Native,

    /// <summary>The page-accounting print: the three pinned inline scripts run, the layout signals when it is done, and the band is written after layout.</summary>
    Accounted,
}

/// <summary>One print, whatever its mode: what to serve, how, and where the printed file goes.</summary>
/// <param name="Document">The composed block followed by the page rule.</param>
/// <param name="PageRule">The page rule the document carries, which the policy allows by its hash.</param>
/// <param name="Files">The files of the taken package.</param>
/// <param name="Mode">Which print this is.</param>
/// <param name="Accounting">The accounting facts and the reserved band, or <see langword="null"/> for the native path.</param>
/// <param name="PdfPath">The file the document is printed into.</param>
public sealed record PdfPrintRequest(
    string Document,
    PageRule PageRule,
    ITemplatePackageFiles Files,
    PdfPrintMode Mode,
    AccountingLayoutFacts? Accounting,
    string PdfPath)
{
    /// <summary>Whether this is the accounting print.</summary>
    public bool IsAccounted => Mode == PdfPrintMode.Accounted;
}

/// <summary>
/// The small report the accounting page publishes when its layout ends: the counts the
/// in-render guard reads. It never carries an accounting value, and the verifier never
/// takes an expected value from it.
/// </summary>
/// <param name="Phase">The phase the script reached.</param>
/// <param name="Pages">How many pages the layout produced.</param>
/// <param name="Rows">How many detail rows the block holds.</param>
/// <param name="SplitRows">How many rows were split.</param>
/// <param name="MaxParts">The most parts one row produced.</param>
/// <param name="PartOverLimit">How many parts exceeded the part limit.</param>
/// <param name="UnfinishedLastPage">Whether a row continues past the last page.</param>
/// <param name="AccountingError">Whether the script's own accounting step raised.</param>
/// <param name="Error">The script's own failure text, if any.</param>
public sealed record LayoutGuardReport(
    string? Phase = null,
    int Pages = 0,
    int Rows = 0,
    int SplitRows = 0,
    int MaxParts = 0,
    int PartOverLimit = 0,
    bool UnfinishedLastPage = false,
    string? AccountingError = null,
    string? Error = null)
{
    /// <summary>How many parts one row may produce before the guard fails the block.</summary>
    public const int MaximumParts = 220;

    /// <summary>A report that says the layout never reported: the done signal was not observed.</summary>
    public static LayoutGuardReport Missing { get; } = new(Phase: "missing", Error: "no-guard-report");

    /// <summary>Whether the guard fails the block.</summary>
    public bool Breached =>
        Phase is null
        || !string.Equals(Phase, "done", StringComparison.Ordinal)
        || PartOverLimit > 0
        || MaxParts > MaximumParts
        || UnfinishedLastPage
        || AccountingError is not null
        || Error is not null;
}
