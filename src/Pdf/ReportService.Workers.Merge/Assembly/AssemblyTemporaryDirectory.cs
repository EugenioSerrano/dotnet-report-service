using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Workers.Merge.Assembly;

/// <summary>
/// The quota-checked folder one assembly owns: a directory of its own under the
/// worker's scratch root, removed when it is disposed, whose bytes may never pass
/// three times the parts' own committed bytes plus, when the template declares a
/// per-page code, the most bytes the codes may add: one stored code of at most
/// <see cref="PageCodeContentValidator.MaximumStoredBytes"/> per page. The folder holds the copied parts,
/// the staging batches and the assembled document; a refusal is the assembly's
/// temporary-disk quota, whose cause may be transient.
/// </summary>
public sealed class AssemblyTemporaryDirectory : IDisposable
{
    /// <summary>How many times the parts' own bytes the folder may hold.</summary>
    public const int QuotaFactor = 3;

    /// <summary>Creates the folder under <paramref name="scratchRoot"/>.</summary>
    /// <param name="scratchRoot">The worker's scratch root.</param>
    /// <param name="partBytes">The parts' total committed bytes.</param>
    /// <param name="pages">The parts' total pages; counted only when a code is declared.</param>
    /// <param name="codeDeclared">Whether the template declares a per-page code.</param>
    public AssemblyTemporaryDirectory(string scratchRoot, long partBytes, long pages = 0, bool codeDeclared = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        ArgumentOutOfRangeException.ThrowIfNegative(partBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(pages);
        QuotaBytes = Quota(partBytes, pages, codeDeclared);
        Root = Path.Combine(scratchRoot, "assembly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// The quota of one assembly: three times the parts' bytes, plus one maximum stored
    /// code per page when the template declares a code.
    /// </summary>
    /// <param name="partBytes">The parts' total committed bytes.</param>
    /// <param name="pages">The parts' total pages.</param>
    /// <param name="codeDeclared">Whether the template declares a per-page code.</param>
    public static long Quota(long partBytes, long pages, bool codeDeclared) =>
        (Math.Max(partBytes, 1) * QuotaFactor)
        + (codeDeclared ? pages * PageCodeContentValidator.MaximumStoredBytes : 0);

    /// <summary>The folder this assembly owns.</summary>
    public string Root { get; }

    /// <summary>The most bytes the folder may hold.</summary>
    public long QuotaBytes { get; }

    /// <summary>How many bytes the folder holds now.</summary>
    public long CurrentBytes() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

    /// <summary>Refuses when the folder passed its quota.</summary>
    /// <exception cref="AssemblyQuotaExceededException">The folder holds more than its quota.</exception>
    public void EnsureWithinQuota()
    {
        var current = CurrentBytes();
        if (current > QuotaBytes)
        {
            throw new AssemblyQuotaExceededException(QuotaBytes, current);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder the process cannot remove is left to the retention sweep.
        }
    }
}

/// <summary>The assembly's temporary folder passed its quota.</summary>
public sealed class AssemblyQuotaExceededException : Exception
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="limitBytes">The quota the folder passed.</param>
    /// <param name="reachedBytes">The bytes the folder holds.</param>
    public AssemblyQuotaExceededException(long limitBytes, long reachedBytes)
        : base($"the assembly's temporary folder passed its quota: limit {limitBytes}, reached {reachedBytes}")
    {
        LimitBytes = limitBytes;
        ReachedBytes = reachedBytes;
    }

    /// <summary>The quota the folder passed.</summary>
    public long LimitBytes { get; }

    /// <summary>The bytes the folder holds.</summary>
    public long ReachedBytes { get; }
}
