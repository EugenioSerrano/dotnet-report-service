namespace ReportService.Jobs.Tests;

/// <summary>
/// The reason an exhausted attempt carries: one token per cause, the two deadline classes and
/// the no-cause token, and nothing else. Each token is fixed text, carries no value - not a
/// path, not a storage key, not a host and not an exception's text - and is read back only in
/// the shape this service writes.
/// </summary>
public sealed class FailureReasonTests
{
    [Fact]
    public void EveryCauseHasItsFixedExhaustionToken()
    {
        Assert.Equal("exhausted cause=watchdog-deadline", AttemptCauseVocabulary.ReasonOf(AttemptCauseVocabulary.WatchdogDeadline));
        Assert.Equal("exhausted cause=guard-deadline", AttemptCauseVocabulary.ReasonOf(AttemptCauseVocabulary.GuardDeadline));
        Assert.Equal("exhausted cause=none", AttemptCauseVocabulary.ReasonOf(null));
    }

    [Theory]
    [InlineData("exhausted cause=watchdog-deadline")]
    [InlineData("exhausted cause=guard-deadline")]
    [InlineData("exhausted cause=none")]
    public void TheThreeTokensAreTheOnlyReasonsThisBuildWrites(string reason)
    {
        Assert.True(AttemptCauseVocabulary.IsExhaustionReason(reason));
    }

    [Theory]
    [InlineData("exhausted cause=render-timeout")]
    [InlineData("exhausted cause=")]
    [InlineData("exhausted cause")]
    [InlineData("the block was not printed within 60 s")]
    [InlineData("C:\\scratch\\report-service-render\\block-0.pdf")]
    [InlineData("outputs/job-1/output-1/3f2a")]
    [InlineData("System.TimeoutException")]
    [InlineData("exhausted cause=none.")]
    public void AReasonOfAnotherShapeIsNotAnExhaustionReason(string reason)
    {
        Assert.False(AttemptCauseVocabulary.IsExhaustionReason(reason));
    }

    [Fact]
    public void TheTwoDeadlineClassesAreTheWholeVocabulary()
    {
        Assert.Equal(["watchdog-deadline", "guard-deadline"], AttemptCauseVocabulary.Deadlines);
        Assert.True(AttemptCauseVocabulary.IsDeadline(AttemptCauseVocabulary.WatchdogDeadline));
        Assert.True(AttemptCauseVocabulary.IsDeadline(AttemptCauseVocabulary.GuardDeadline));
        Assert.False(AttemptCauseVocabulary.IsDeadline(null));
        Assert.False(AttemptCauseVocabulary.IsDeadline(AttemptCauseVocabulary.None));
    }
}
