namespace ReportService.Pdf.Assembly;

/// <summary>
/// The fixed bounds of an assembly's method: the number of consecutive parts one
/// staging batch imports, and the multiple of the parts' own bytes the assembly may
/// use on temporary disk. A document's size is bounded by the deployment's configured
/// capacity, which a caller passes on <see cref="AssemblyRequest"/>, never by a value
/// of this library.
/// </summary>
public static class AssemblyLimits
{
    /// <summary>How many consecutive parts one staging batch imports.</summary>
    public const int BatchSize = 25;

    /// <summary>How many times the parts' own bytes the assembly may use on temporary disk.</summary>
    public const int TemporaryDiskFactor = 3;
}
