using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplateSource.Checks;

/// <summary>
/// What a take-time check is given: the version being taken, its manifest and
/// the directory the package was copied to. A check reads and never writes; it
/// answers whether the version may be taken, and what an author must fix when
/// it may not.
/// </summary>
/// <param name="TemplateId">The template's identifier.</param>
/// <param name="Version">The version being taken.</param>
/// <param name="Manifest">The typed view of the manifest.</param>
/// <param name="PackageDirectory">The directory the package's files were copied to.</param>
/// <param name="CommitSha">The commit they were read at.</param>
public sealed record TemplateCandidate(
    string TemplateId,
    string Version,
    TemplateManifest Manifest,
    string PackageDirectory,
    string CommitSha);

/// <summary>What one check decided.</summary>
public sealed record TemplateCheckResult
{
    private TemplateCheckResult(bool passed, string? code, string? reason)
    {
        Passed = passed;
        Code = code;
        Reason = reason;
    }

    /// <summary>True when the check accepted the version.</summary>
    public bool Passed { get; }

    /// <summary>The error code of a refusal, one of <see cref="TemplateCheckCodes"/>.</summary>
    public string? Code { get; }

    /// <summary>What the author must fix; constant text that names files and fields, never a value.</summary>
    public string? Reason { get; }

    /// <summary>The check accepted the version.</summary>
    public static TemplateCheckResult Pass() => new(true, null, null);

    /// <summary>The check refused the version.</summary>
    /// <param name="code">One of <see cref="TemplateCheckCodes"/>.</param>
    /// <param name="reason">What the author must fix.</param>
    public static TemplateCheckResult Refuse(string code, string reason) => new(false, code, reason);
}

/// <summary>The codes a check may refuse with.</summary>
public static class TemplateCheckCodes
{
    /// <summary>The package is not a valid template package.</summary>
    public const string TemplateInvalid = "TEMPLATE_INVALID";

    /// <summary>The package asks for something this deployment cannot do.</summary>
    public const string UnsupportedCapability = "UNSUPPORTED_CAPABILITY";
}

/// <summary>
/// One check a version passes before it is taken. This build registers none:
/// the schema and syntax checks, the sample render and the PDF constraints are
/// added by later changes, each as a check of its own, so a refusal always
/// names the check that made it.
/// </summary>
public interface ITemplateVersionCheck
{
    /// <summary>The check's name, as a log record says it.</summary>
    string Name { get; }

    /// <summary>Checks one candidate version.</summary>
    /// <param name="candidate">The version being taken.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>Runs the registered checks, in the order they were registered.</summary>
public static class TemplateVersionChecks
{
    /// <summary>Runs every check, stopping at the first refusal.</summary>
    /// <param name="checks">The registered checks.</param>
    /// <param name="candidate">The version being taken.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The first refusal, or a pass when every check accepted the version.</returns>
    public static async Task<TemplateCheckResult> RunAsync(
        IEnumerable<ITemplateVersionCheck> checks,
        TemplateCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(candidate);
        foreach (var check in checks)
        {
            var result = await check.CheckAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!result.Passed)
            {
                return result;
            }
        }

        return TemplateCheckResult.Pass();
    }
}
