namespace ReportService.TemplateSource;

/// <summary>
/// What the source told the take. The four observations are kept apart because
/// they mean different things to a job: an unreachable source may be retried
/// later, a misconfigured one is the operator's to fix, and a package the
/// source cannot deliver is the template author's. None of them is ever
/// reported as "the version does not exist".
/// </summary>
public enum TemplateSourceFailure
{
    /// <summary>The source could not be reached, did not answer in time, answered with a server fault, or answered with bytes that did not verify.</summary>
    Unreachable,

    /// <summary>The source answered that the repository, the branch or the credential is not usable.</summary>
    Misconfigured,

    /// <summary>The package exceeds a bound this deployment sets: too many files, a file too large, a package too large, a truncated tree.</summary>
    PackageTooLarge,

    /// <summary>The package holds an entry this service does not store, such as a symbolic link or a submodule.</summary>
    UnsupportedEntry,
}

/// <summary>
/// The source could not deliver what was asked. The kind says which of the four
/// observations it was; the message names what a template author or an operator
/// has to change and never a token, a URL or a response body.
/// </summary>
public sealed class TemplateSourceException : Exception
{
    /// <summary>The source did not deliver what was asked.</summary>
    /// <param name="failure">Which observation this is.</param>
    /// <param name="message">What to fix or what to retry.</param>
    public TemplateSourceException(TemplateSourceFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    /// <summary>The source did not deliver what was asked; <paramref name="innerException"/> says why.</summary>
    /// <param name="failure">Which observation this is.</param>
    /// <param name="message">What to fix or what to retry.</param>
    /// <param name="innerException">The cause, for the log.</param>
    public TemplateSourceException(TemplateSourceFailure failure, string message, Exception innerException)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Which observation this is.</summary>
    public TemplateSourceFailure Failure { get; }

    /// <summary>True when retrying the same read later may succeed.</summary>
    public bool Retryable => Failure is TemplateSourceFailure.Unreachable or TemplateSourceFailure.Misconfigured;
}
