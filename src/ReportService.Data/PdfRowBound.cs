namespace ReportService.Data;

/// <summary>
/// The row bound a PDF detail record is admitted under: the most bytes of UTF-8 one string
/// value of a record may carry, measured after the JSON escapes are resolved. It is a
/// measured constant of this service version and never a setting, because the printed
/// envelope was measured with it and a deployment that changed it would leave that envelope
/// silently. The header is not a row and is not covered; each value is measured on its own,
/// whatever depth of the record it sits at.
/// </summary>
public static class PdfRowBound
{
    /// <summary>The most bytes of decoded UTF-8 one string value of a detail record may carry.</summary>
    public const int Bytes = 262_144;
}
