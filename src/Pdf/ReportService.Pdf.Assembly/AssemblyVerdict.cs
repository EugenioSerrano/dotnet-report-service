namespace ReportService.Pdf.Assembly;

/// <summary>The reason an assembled document failed its structural verification.</summary>
public static class AssemblyFailureReasons
{
    /// <summary>A part's bytes do not match the size or hash its commit recorded.</summary>
    public const string PartBytes = "part-bytes";

    /// <summary>A part's page count does not match its commit.</summary>
    public const string PartPages = "part-pages";

    /// <summary>A part's page box does not equal the template's page size.</summary>
    public const string PartPageBox = "part-page-box";

    /// <summary>The assembled page count does not equal the sum of the parts' pages.</summary>
    public const string PageCount = "page-count";

    /// <summary>A page's text outside the numbering area does not equal its source page's.</summary>
    public const string Order = "order";

    /// <summary>The numbering area does not hold exactly the expected stamp.</summary>
    public const string Stamp = "stamp";

    /// <summary>A page dictionary differs from its source page's in a way the architecture does not allow.</summary>
    public const string PageDictionary = "page-dictionary";

    /// <summary>The assembled document cannot be read.</summary>
    public const string Unreadable = "unreadable";
}

/// <summary>
/// The verdict of one structural verification: whether the assembled document is
/// exactly the parts in manifest order with one stamp per page, and, when it is
/// not, the check that failed and the page or part it failed on. The verdict
/// carries no record value and no path.
/// </summary>
/// <param name="Passed">Whether the document passed every check.</param>
/// <param name="Reason">The failed check's reason, or <see langword="null"/> when it passed.</param>
/// <param name="PartIndex">The part the failure was found in, or <see langword="null"/>.</param>
/// <param name="PageNumber">The page the failure was found on, or <see langword="null"/>.</param>
public sealed record AssemblyVerdict(bool Passed, string? Reason, int? PartIndex, int? PageNumber)
{
    /// <summary>The verdict of a document that passed.</summary>
    public static AssemblyVerdict Good { get; } = new(true, null, null, null);

    /// <summary>A verdict that failed a check.</summary>
    /// <param name="reason">The failed check's reason.</param>
    /// <param name="partIndex">The part, when the check has one.</param>
    /// <param name="pageNumber">The page, when the check has one.</param>
    public static AssemblyVerdict Failed(string reason, int? partIndex = null, int? pageNumber = null) =>
        new(false, reason, partIndex, pageNumber);
}

/// <summary>One part as the verification reads it: the file and what its commit recorded.</summary>
/// <param name="PartIndex">The part's position in manifest order.</param>
/// <param name="Path">The part's temporary file.</param>
/// <param name="PageCount">The page count recorded at commit.</param>
/// <param name="SizeBytes">The size recorded at commit.</param>
/// <param name="Sha256">The SHA-256 recorded at commit, lower-case hexadecimal.</param>
public sealed record AssemblyPartExpectation(int PartIndex, string Path, int PageCount, long SizeBytes, string Sha256);
