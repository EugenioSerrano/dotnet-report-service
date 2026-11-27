namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// A clock fixed to one instant for a whole command. The sample renderer reads the
/// sample's presentation instant from its time provider and the tool composes the
/// same sample once more for the HTML it writes; one fixed instant keeps the two
/// documents of one run consistent and the render deterministic.
/// </summary>
/// <param name="instant">The instant every read answers.</param>
public sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
{
    private readonly DateTimeOffset _instant = instant.ToUniversalTime();

    /// <summary>The instant this clock answers, in UTC.</summary>
    public DateTimeOffset Instant => _instant;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _instant;
}
