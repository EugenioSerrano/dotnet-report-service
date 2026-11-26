namespace ReportService.Pdf.Assembly;

/// <summary>
/// The fixed bounds of an assembly: the document ceiling a finished report may not
/// pass, the number of consecutive parts one staging batch imports, and the
/// multiple of the parts' own bytes the assembly may use on temporary disk.
/// </summary>
public static class AssemblyLimits
{
    /// <summary>The largest page count a document may have: 10 000 pages.</summary>
    public const int MaximumPages = 10_000;

    /// <summary>The largest total size of a document's parts: 36 MiB (37 748 736 bytes).</summary>
    public const long MaximumPartBytes = 37_748_736L;

    /// <summary>How many consecutive parts one staging batch imports.</summary>
    public const int BatchSize = 25;

    /// <summary>How many times the parts' own bytes the assembly may use on temporary disk.</summary>
    public const int TemporaryDiskFactor = 3;
}
