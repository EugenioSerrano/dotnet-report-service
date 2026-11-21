namespace ReportService.Pdf.Verification;

/// <summary>
/// Pages: a document with no page at all, or with a page that prints no word at all,
/// is never delivered. The empty report is held to the same rule - its header needs
/// pages, and a page the header does not reach is a blank page - which is why the
/// check reads the page itself and not the block's record count.
/// </summary>
public sealed class PageCheck
{
    /// <summary>Checks the document's pages.</summary>
    /// <param name="reading">The document as it was read.</param>
    public SourceCheckOutcome Check(PdfReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.Pages.Count == 0)
        {
            return SourceCheckOutcome.Fail(VerificationReasons.NoPage);
        }

        for (var index = 0; index < reading.Pages.Count; index++)
        {
            if (reading.Pages[index].Words.Count == 0)
            {
                return SourceCheckOutcome.Fail(VerificationReasons.BlankPage);
            }
        }

        return SourceCheckOutcome.Pass;
    }
}
