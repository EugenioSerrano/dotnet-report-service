namespace ReportService.Jobs;

/// <summary>
/// The two deadline classes a rendering attempt can record, and the reason token a
/// failed output carries from them. The vocabulary is closed: a cause that is not one
/// of the two is refused by the schema and by the in-memory store alike, so the record
/// can never drift into a second status or into free text.
/// </summary>
public static class AttemptCauseVocabulary
{
    /// <summary>The cause the watchdog records when a render passed its deadline and the grace.</summary>
    public const string WatchdogDeadline = "watchdog-deadline";

    /// <summary>The cause the composition guard records when a block's HTML passed its deadline.</summary>
    public const string GuardDeadline = "guard-deadline";

    /// <summary>The token a reason carries when no cause was recorded at all.</summary>
    public const string None = "none";

    /// <summary>The two deadline classes, in the schema's order.</summary>
    public static IReadOnlyList<string> Deadlines { get; } = [WatchdogDeadline, GuardDeadline];

    /// <summary>The reason token of an exhausted attempt: the recorded cause, or none.</summary>
    /// <param name="cause">The last recorded cause, or <see langword="null"/> when none was.</param>
    /// <returns>The token a failed output stores beside its code.</returns>
    public static string ReasonOf(string? cause) => "exhausted cause=" + (cause ?? None);

    /// <summary>Whether <paramref name="cause"/> is one of the two deadline classes.</summary>
    /// <param name="cause">The cause read from a row or received from a caller.</param>
    public static bool IsDeadline(string? cause) =>
        string.Equals(cause, WatchdogDeadline, StringComparison.Ordinal)
        || string.Equals(cause, GuardDeadline, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="reason"/> is one of the three tokens this service writes for
    /// an exhausted attempt: the two deadline classes and the no-cause token. A reason of any
    /// other shape is not this build's, and no consumer sentence is built from it.
    /// </summary>
    /// <param name="reason">The stored reason recorded beside a failure code.</param>
    public static bool IsExhaustionReason(string? reason) =>
        string.Equals(reason, ReasonOf(WatchdogDeadline), StringComparison.Ordinal)
        || string.Equals(reason, ReasonOf(GuardDeadline), StringComparison.Ordinal)
        || string.Equals(reason, ReasonOf(null), StringComparison.Ordinal);
}
