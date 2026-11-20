using System.Globalization;

namespace ReportService.Data;

/// <summary>
/// The reason a PDF output is refused by the row bound: the one diagnostic sentence every data
/// refusal of this service is written with, carrying the code, the reason word with the field's
/// size and the bound, the limit's own token, the record's ordinal and start offset and the
/// field's path relative to the record - and never a value of the document. The sentence is
/// built by the profile's own builder, so a stored reason keeps one grammar and one key order
/// whatever refused the data.
/// </summary>
public static class RowBoundDiagnostic
{
    /// <summary>The reason word this code carries, beside the two numbers the bound adds.</summary>
    private const string FieldTooLarge = "field-too-large";

    /// <summary>The limit this code names.</summary>
    private const string Limit = "row-bound";

    /// <summary>Builds the stored reason of one PDF output refused by the row bound.</summary>
    /// <param name="ordinal">The ordinal of the record the value was found in; 0 is the header.</param>
    /// <param name="offset">Where the record starts, in bytes of the uncompressed document.</param>
    /// <param name="size">The field's decoded length in bytes.</param>
    /// <param name="bound">The bound the field crossed.</param>
    /// <param name="path">The field's path relative to the record, as the renderer answered it.</param>
    /// <returns>The reason, as the output's row and the operator's log carry it.</returns>
    public static string For(int ordinal, long offset, long size, long bound, string path) =>
        new DataContractViolation(
            DataContractViolation.RowBoundExceeded,
            string.Create(CultureInfo.InvariantCulture, $"{FieldTooLarge} size={size} bound={bound}"),
            Limit,
            ordinal,
            offset,
            path).Message;
}
