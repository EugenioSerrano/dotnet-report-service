using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.Jobs.Tests;

/// <summary>
/// The attempt-cause record on the in-memory store: the first row of an attempt wins even
/// when a second arrives with another deadline class, the read answers the greatest attempt,
/// a block with no row answers none, and another output's rows are never this output's. The
/// same semantics are held against the real database by the shared store contract; these
/// cases read them on the double the component tests use, so a drift is caught before the
/// integration cycle.
/// </summary>
public sealed class AttemptCauseStoreTests
{
    private const string JobId = "job-0123456789abcdef0123456789abcdef";

    private const string OutputId = "output-0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset Now = new(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);

    private static InMemoryJobStore StoreWithOneOutput()
    {
        var store = new InMemoryJobStore(new InMemoryUploadStore());
        store.SeedOutput(JobId, OutputId, OutputFormat.Pdf);
        return store;
    }

    [Fact]
    public async Task TheFirstCauseOfAnAttemptWinsEvenWhenASecondArrives()
    {
        var store = StoreWithOneOutput();

        Assert.Equal(
            AttemptCauseOutcome.Recorded,
            await store.RecordAttemptCauseAsync(
                new AttemptCause(OutputId, 0, 1, AttemptCauseVocabulary.GuardDeadline, Now),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            AttemptCauseOutcome.AlreadyRecorded,
            await store.RecordAttemptCauseAsync(
                new AttemptCause(OutputId, 0, 1, AttemptCauseVocabulary.WatchdogDeadline, Now.AddSeconds(1)),
                TestContext.Current.CancellationToken));

        var row = await store.FindLastAttemptCauseAsync(OutputId, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(row);
        Assert.Equal(AttemptCauseVocabulary.GuardDeadline, row!.Cause);
        Assert.Equal(Now, row.RecordedAt);
    }

    [Fact]
    public async Task TheLastCauseAcrossAttemptsIsTheOneRead()
    {
        var store = StoreWithOneOutput();
        var cancellationToken = TestContext.Current.CancellationToken;

        await store.RecordAttemptCauseAsync(new AttemptCause(OutputId, 0, 1, AttemptCauseVocabulary.WatchdogDeadline, Now), cancellationToken);
        await store.RecordAttemptCauseAsync(new AttemptCause(OutputId, 0, 2, AttemptCauseVocabulary.WatchdogDeadline, Now.AddMinutes(1)), cancellationToken);
        await store.RecordAttemptCauseAsync(new AttemptCause(OutputId, 0, 3, AttemptCauseVocabulary.GuardDeadline, Now.AddMinutes(2)), cancellationToken);

        var last = await store.FindLastAttemptCauseAsync(OutputId, 0, cancellationToken);

        Assert.NotNull(last);
        Assert.Equal(3, last!.Attempt);
        Assert.Equal(AttemptCauseVocabulary.GuardDeadline, last.Cause);
        Assert.Equal(Now.AddMinutes(2), last.RecordedAt);
    }

    [Fact]
    public async Task AnAttemptThatRecordedNothingHasNoRow()
    {
        var store = StoreWithOneOutput();

        await store.RecordAttemptCauseAsync(
            new AttemptCause(OutputId, 0, 1, AttemptCauseVocabulary.GuardDeadline, Now),
            TestContext.Current.CancellationToken);

        Assert.Null(await store.FindLastAttemptCauseAsync(OutputId, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnotherOutputsCauseIsInvisible()
    {
        const string OtherOutputId = "output-fedcba9876543210fedcba9876543210";
        var store = StoreWithOneOutput();
        store.SeedOutput(JobId, OtherOutputId, OutputFormat.Pdf);
        var cancellationToken = TestContext.Current.CancellationToken;

        await store.RecordAttemptCauseAsync(new AttemptCause(OutputId, 0, 1, AttemptCauseVocabulary.GuardDeadline, Now), cancellationToken);
        await store.RecordAttemptCauseAsync(new AttemptCause(OtherOutputId, 0, 1, AttemptCauseVocabulary.WatchdogDeadline, Now), cancellationToken);

        Assert.Equal(
            AttemptCauseVocabulary.GuardDeadline,
            (await store.FindLastAttemptCauseAsync(OutputId, 0, cancellationToken))!.Cause);
        Assert.Equal(
            AttemptCauseVocabulary.WatchdogDeadline,
            (await store.FindLastAttemptCauseAsync(OtherOutputId, 0, cancellationToken))!.Cause);
    }

    [Fact]
    public async Task ACauseThatIsNotADeadlineClassIsRefused()
    {
        var store = StoreWithOneOutput();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RecordAttemptCauseAsync(
            new AttemptCause(OutputId, 0, 1, "render-timeout", Now),
            TestContext.Current.CancellationToken));
        Assert.Null(await store.FindLastAttemptCauseAsync(OutputId, 0, TestContext.Current.CancellationToken));
    }

    // The delivered reason projection - the failed output's reason reaching the job with its
    // code - is asserted by the shared contract in this same project
    // (TheReasonOfAFailedOutputIsReadBackAndCopiedToTheJob), which runs against this double
    // and against the real database. It is not restated here.
}
