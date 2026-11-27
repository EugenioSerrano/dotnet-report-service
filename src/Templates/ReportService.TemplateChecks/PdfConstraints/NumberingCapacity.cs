namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>
/// The page capacity the numbering check measures a stamp at: the most pages a
/// document of the hosting process may reach, as that process resolved it from its
/// configuration. It carries no value of its own - the process that runs the checks
/// supplies it.
/// </summary>
public sealed record NumberingCapacity
{
    /// <summary>Creates the capacity.</summary>
    /// <param name="maximumPages">The most pages a document may reach; greater than zero.</param>
    public NumberingCapacity(int maximumPages)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPages);
        MaximumPages = maximumPages;
    }

    /// <summary>The most pages a document may reach.</summary>
    public int MaximumPages { get; }
}
