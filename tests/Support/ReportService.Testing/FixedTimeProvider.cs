namespace ReportService.Testing;

/// <summary>
/// A clock that never moves: a test fixes the instant a session's timestamps are
/// taken from, so a creation response and a later read can be compared field for
/// field and a truncated value can be asserted exactly.
/// </summary>
public sealed class FixedTimeProvider : TimeProvider
{
    /// <summary>Creates the clock at <paramref name="now"/>.</summary>
    /// <param name="now">The instant every read of the clock answers.</param>
    public FixedTimeProvider(DateTimeOffset now) => Now = now;

    /// <summary>The instant this clock answers.</summary>
    public DateTimeOffset Now { get; }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;
}
