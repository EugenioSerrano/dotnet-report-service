using ReportService.Jobs;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The map an output ends with when it has no attempt left: a refusal of the data or the
/// template keeps the code that raised it and reads nothing, while every other failure reads
/// the last cause the block recorded - the read is doubled here, so the case asserts when it
/// runs and nothing else does - and answers the timeout or the generic code from it. A crash
/// records no cause, which is what makes the generic code its answer.
/// </summary>
public sealed class PdfOutputExhaustionTests
{
    /// <summary>The refusal's own code, no read, and no reason to report.</summary>
    [Theory]
    [InlineData("PAGINATION_FAILED")]
    [InlineData("DATA_INVALID")]
    [InlineData("TEMPLATE_INVALID")]
    [InlineData("UNSUPPORTED_CAPABILITY")]
    [InlineData("QUOTA_EXCEEDED")]
    public async Task ARefusalKeepsItsCodeAndReadsNoCause(string code)
    {
        var read = new CountingRead(null);

        var (mapped, reason) = await ReportPdfOutputWorkflow.ExhaustionAsync(code, read.ReadAsync);

        Assert.Equal(code, mapped);
        Assert.Null(reason);
        Assert.Equal(0, read.Calls);
    }

    /// <summary>
    /// The composition guard's deadline: the attempt carried the timeout and recorded its
    /// cause, so the output keeps the timeout and its reason names the cause the read supplied.
    /// </summary>
    [Fact]
    public async Task ATimeoutWithARecordedGuardDeadlineKeepsTheTimeoutAndNamesTheCause()
    {
        var read = new CountingRead(new LastAttemptCause(3, AttemptCauseVocabulary.GuardDeadline));

        var (code, reason) = await ReportPdfOutputWorkflow.ExhaustionAsync("RENDER_TIMEOUT", read.ReadAsync);

        Assert.Equal("RENDER_TIMEOUT", code);
        Assert.Equal("exhausted cause=guard-deadline", reason);
        Assert.Equal(1, read.Calls);
    }

    /// <summary>
    /// A worker that stopped carries no code at all and recorded its cause before it ended:
    /// the read supplies the watchdog's cause and the output ends as the timeout it was.
    /// </summary>
    [Fact]
    public async Task AWorkerThatStoppedEndsAsTheTimeoutItsRecordedCauseNames()
    {
        var read = new CountingRead(new LastAttemptCause(3, AttemptCauseVocabulary.WatchdogDeadline));

        var (code, reason) = await ReportPdfOutputWorkflow.ExhaustionAsync(null, read.ReadAsync);

        Assert.Equal("RENDER_TIMEOUT", code);
        Assert.Equal("exhausted cause=watchdog-deadline", reason);
        Assert.Equal(1, read.Calls);
    }

    /// <summary>The generic code is not a shortcut: it reads, and a crash records nothing.</summary>
    [Fact]
    public async Task TheGenericCodeReadsAndACrashRecordsNoCause()
    {
        var read = new CountingRead(null);

        var (code, reason) = await ReportPdfOutputWorkflow.ExhaustionAsync("EXECUTION_FAILED", read.ReadAsync);

        Assert.Equal(ReportPdfOutputWorkflow.ExecutionFailed, code);
        Assert.Equal("exhausted cause=none", reason);
        Assert.Equal(1, read.Calls);
    }

    /// <summary>With nothing recorded at all, the generic code is the answer and the token says so.</summary>
    [Fact]
    public async Task NoRecordedCauseEndsWithTheGenericCodeAndTheNoneToken()
    {
        var read = new CountingRead(null);

        var (code, reason) = await ReportPdfOutputWorkflow.ExhaustionAsync(null, read.ReadAsync);

        Assert.Equal(ReportPdfOutputWorkflow.ExecutionFailed, code);
        Assert.Equal("exhausted cause=none", reason);
        Assert.Equal(1, read.Calls);
    }

    /// <summary>
    /// The rule is the last recorded cause, and the store answers the row with the greatest
    /// attempt: a cause recorded by the first attempt and never replaced still decides the
    /// output, because a later attempt that crashed could not write one.
    /// </summary>
    [Fact]
    public async Task ACauseRecordedByAnEarlierAttemptStillDecidesTheOutput()
    {
        var read = new CountingRead(new LastAttemptCause(1, AttemptCauseVocabulary.WatchdogDeadline));

        var (code, reason) = await ReportPdfOutputWorkflow.ExhaustionAsync(null, read.ReadAsync);

        Assert.Equal("RENDER_TIMEOUT", code);
        Assert.Equal("exhausted cause=watchdog-deadline", reason);
        Assert.Equal(1, read.Calls);
    }

    /// <summary>
    /// A carried type the path does not raise is not a consumer's code: it reads like any
    /// other failure and never ends the output under the type's own name.
    /// </summary>
    [Fact]
    public async Task ACarriedTypeThePathDoesNotRaiseNeverBecomesTheCode()
    {
        var read = new CountingRead(new LastAttemptCause(2, AttemptCauseVocabulary.GuardDeadline));

        var (code, reason) = await ReportPdfOutputWorkflow.ExhaustionAsync("InvalidOperationException", read.ReadAsync);

        Assert.Equal("RENDER_TIMEOUT", code);
        Assert.Equal("exhausted cause=guard-deadline", reason);
        Assert.Equal(1, read.Calls);
    }

    /// <summary>The token is the vocabulary's own, so the formatter reads it unchanged.</summary>
    [Theory]
    [InlineData(AttemptCauseVocabulary.GuardDeadline, "exhausted cause=guard-deadline")]
    [InlineData(AttemptCauseVocabulary.WatchdogDeadline, "exhausted cause=watchdog-deadline")]
    public async Task EveryReasonIsOneOfTheTokensTheFormatterKnows(string cause, string expected)
    {
        var read = new CountingRead(new LastAttemptCause(3, cause));

        var (_, reason) = await ReportPdfOutputWorkflow.ExhaustionAsync(null, read.ReadAsync);

        Assert.Equal(expected, reason);
        Assert.True(AttemptCauseVocabulary.IsExhaustionReason(reason));
    }

    /// <summary>A doubled read: it answers the row it was given and counts its calls.</summary>
    /// <summary>An image-source refusal keeps its code and its message as the reason, and reads nothing.</summary>
    [Fact]
    public async Task AnImageSourceRefusalKeepsItsCodeAndItsDiagnostic()
    {
        var read = new CountingRead(null);

        var (code, reason) = await ReportPdfOutputWorkflow.ExhaustionAsync("IMAGE_SOURCE_INVALID", "part=row ordinal=3 attribute=src", read.ReadAsync);

        Assert.Equal("IMAGE_SOURCE_INVALID", code);
        Assert.Equal("part=row ordinal=3 attribute=src", reason);
        Assert.Equal(0, read.Calls);
    }

    /// <summary>Another refusal keeps no reason even when its failure carries a message.</summary>
    [Fact]
    public async Task AnotherRefusalKeepsNoReasonFromItsMessage()
    {
        var read = new CountingRead(null);

        var (code, reason) = await ReportPdfOutputWorkflow.ExhaustionAsync("QUOTA_EXCEEDED", "the composed text passed", read.ReadAsync);

        Assert.Equal("QUOTA_EXCEEDED", code);
        Assert.Null(reason);
    }

    private sealed class CountingRead(LastAttemptCause? row)
    {
        /// <summary>How many times the map asked for the cause.</summary>
        public int Calls { get; private set; }

        /// <summary>Answers the row the case gave.</summary>
        /// <returns>The row, or null when the case gave none.</returns>
        public Task<LastAttemptCause?> ReadAsync()
        {
            Calls++;
            return Task.FromResult(row);
        }
    }
}
