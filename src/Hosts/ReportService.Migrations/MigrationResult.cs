namespace ReportService.Migrations;

/// <summary>
/// A run that stopped on a script: the script, the position the server reported,
/// its SQLSTATE and its message. A failure before any script — a connection that
/// cannot be opened, a lock that cannot be taken — names no script and carries
/// the exception's type only, never its message, which can name a host or a
/// user.
/// </summary>
public sealed record MigrationFailure(
    string? ScriptName,
    int Position,
    string? SqlState,
    string ExceptionType,
    string Message)
{
    /// <summary>The line written to standard error.</summary>
    public string Line => ScriptName is null
        ? $"migration run failed before any script was applied: {ExceptionType}"
        : $"migration '{ScriptName}' failed at position {Position}: {SqlState ?? "no SQLSTATE"} {Message}".TrimEnd();
}

/// <summary>What one run did: the scripts it applied, those already applied, the version it reached, and why it stopped.</summary>
public sealed record MigrationResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> AlreadyApplied,
    string CurrentVersion,
    IReadOnlyList<string> Refusals,
    MigrationFailure? Failure)
{
    /// <summary>Whether the run applied everything the code embeds and the schema is current.</summary>
    public bool Succeeded => Failure is null && Refusals.Count == 0;

    /// <summary>The lines of a successful run; the scripts applied, then the version reached.</summary>
    public IReadOnlyList<string> OutputLines => Succeeded
        ? [.. Applied.Select(name => $"applied '{name}'"), StatusLine]
        : [.. Applied.Select(name => $"applied '{name}'")];

    /// <summary>The lines of a refused or failed run.</summary>
    public IReadOnlyList<string> ErrorLines => Failure is null ? Refusals : [Failure.Line];

    /// <summary>The version the schema is at, as the run reports it.</summary>
    public string StatusLine => Applied.Count == 0
        ? $"no change; schema is current at {CurrentVersion}"
        : $"schema is current at {CurrentVersion}";

    /// <summary>Every line the run writes, in order.</summary>
    public IReadOnlyList<string> Lines() => [.. OutputLines, .. ErrorLines];
}
