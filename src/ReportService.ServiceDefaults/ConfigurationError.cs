namespace ReportService.ServiceDefaults;

/// <summary>
/// Raised when a process's configuration is missing or invalid. The message
/// names every faulty setting and its reason, in the form
/// <c>setting '&lt;path&gt;': &lt;reason&gt;</c>; it never carries a setting's
/// value, and nothing is stored in <see cref="Exception.Data"/>, so a secret
/// cannot leak through an exception dump.
/// </summary>
public sealed class ConfigurationError : Exception
{
    /// <summary>Creates the error for one setting.</summary>
    public ConfigurationError(string setting, string reason)
        : this([new ConfigurationProblem(setting, reason)])
    {
    }

    /// <summary>Creates the error for one or more problems, reported together.</summary>
    public ConfigurationError(IReadOnlyList<ConfigurationProblem> problems)
        : base(Describe(problems))
    {
        Problems = problems;
    }

    /// <summary>Every problem found, in the order found.</summary>
    public IReadOnlyList<ConfigurationProblem> Problems { get; }

    /// <summary>The first faulty setting.</summary>
    public string Setting => Problems[0].Setting;

    /// <summary>The reason the first faulty setting is refused.</summary>
    public string Reason => Problems[0].Reason;

    private static string Describe(IReadOnlyList<ConfigurationProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (problems.Count == 0)
        {
            throw new ArgumentException("A configuration error needs at least one problem.", nameof(problems));
        }

        return string.Join("; ", problems.Select(p => p.ToString()));
    }
}
