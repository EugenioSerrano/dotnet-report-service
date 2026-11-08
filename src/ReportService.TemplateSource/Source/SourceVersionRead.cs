namespace ReportService.TemplateSource.Source;

/// <summary>
/// What one exact read answered: the verified publication, or the one state that
/// fits. <see cref="Absent"/> and <see cref="Incomplete"/> are different things -
/// no record and no file is a version that does not exist, while files without a
/// record are a publication that was never completed - and <see cref="Unreachable"/>
/// is never reported as absence.
/// </summary>
public abstract record SourceVersionRead
{
    private protected SourceVersionRead()
    {
    }

    /// <summary>The publication was found, read verified and passed its own checks.</summary>
    /// <param name="Publication">The verified publication.</param>
    public sealed record Found(SourcePublication Publication) : SourceVersionRead;

    /// <summary>No publication record and no file exist for the version.</summary>
    public sealed record Absent : SourceVersionRead;

    /// <summary>Files exist for the version and no publication record does.</summary>
    /// <param name="FileCount">How many file objects the version's prefix holds.</param>
    public sealed record Incomplete(int FileCount) : SourceVersionRead;

    /// <summary>More than one publication record exists; the read picks none of them.</summary>
    /// <param name="Descriptors">Every record's name, hash and size.</param>
    public sealed record Duplicate(IReadOnlyList<SourceDescriptorName> Descriptors) : SourceVersionRead;

    /// <summary>The class could not be listed or read; retrying the same version may succeed.</summary>
    /// <param name="Reason">The constant text the caller may retry.</param>
    public sealed record Unreachable(string Reason) : SourceVersionRead;

    /// <summary>The record is invalid, or its contract is not one this build reads.</summary>
    /// <param name="Refusal">The kind and the text that names the defect.</param>
    public sealed record Refused(SourceReadRefusal Refusal) : SourceVersionRead;
}

/// <summary>What kind of refusal a record's own defect is.</summary>
public enum SourceReadRefusalKind
{
    /// <summary>The record contradicts its own contract or the requested version.</summary>
    Invalid,

    /// <summary>The record was written to a contract version this build does not read.</summary>
    Unsupported,
}

/// <summary>
/// One refusal of the read, with its kind and the text that names the offending
/// file, field or cap - never a storage key, a credential or a provider message.
/// The product's error vocabulary stays in the take, which maps the kind.
/// </summary>
/// <param name="Kind">Whether the defect is invalid or unsupported.</param>
/// <param name="Reason">What the author has to fix.</param>
public sealed record SourceReadRefusal(SourceReadRefusalKind Kind, string Reason);

/// <summary>What kind of failure reading one file's bytes is.</summary>
public enum SourceReadFailureKind
{
    /// <summary>The record names a file the class does not hold, or the bytes contradict the record.</summary>
    Invalid,

    /// <summary>The file's contract is not one this build reads.</summary>
    Unsupported,

    /// <summary>The class could not be read; retrying the same version may succeed.</summary>
    Unreachable,
}

/// <summary>
/// The failure of one file's verified read. The port's file read streams its bytes
/// to a caller's callback and has no result to carry a refusal, so a refusal is
/// this exception; an exception thrown by the callback itself is the caller's and
/// passes through unchanged.
/// </summary>
public sealed class SourceReadException : Exception
{
    /// <summary>The file could not be read.</summary>
    /// <param name="kind">Whether the defect is invalid, unsupported or an outage.</param>
    /// <param name="reason">What the author or the operator has to act on.</param>
    public SourceReadException(SourceReadFailureKind kind, string reason)
        : base(reason)
    {
        Kind = kind;
    }

    /// <summary>What kind of failure this is.</summary>
    public SourceReadFailureKind Kind { get; }
}
