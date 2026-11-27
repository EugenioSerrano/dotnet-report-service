using ReportService.Jobs;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The read of a block's last recorded cause, as a plain class on the in-memory store: the
/// row with the greatest attempt is the answer, a block no attempt recorded answers an empty
/// result, and a row of another output is never this read's. The step carries the metadata
/// class of settings and is served from the queue the job's own steps run on.
/// </summary>
public sealed class AttemptCauseActivityTests
{
    private static readonly DateTimeOffset Now = ReportJobFixture.Now;

    [Fact]
    public async Task TheLastRecordedCauseIsTheOneTheStepAnswers()
    {
        var stores = ReportJobFixture.Stores();
        var acceptance = await ReportJobFixture.AcceptedAsync(stores);
        var cancellationToken = TestContext.Current.CancellationToken;
        await stores.Jobs.RecordAttemptCauseAsync(
            new AttemptCause(acceptance.Outputs[0].OutputId, 0, 1, AttemptCauseVocabulary.WatchdogDeadline, Now),
            cancellationToken);
        await stores.Jobs.RecordAttemptCauseAsync(
            new AttemptCause(acceptance.Outputs[0].OutputId, 0, 2, AttemptCauseVocabulary.WatchdogDeadline, Now.AddMinutes(1)),
            cancellationToken);
        await stores.Jobs.RecordAttemptCauseAsync(
            new AttemptCause(acceptance.Outputs[0].OutputId, 0, 3, AttemptCauseVocabulary.GuardDeadline, Now.AddMinutes(2)),
            cancellationToken);
        var activity = new AttemptCauseActivities(stores.Jobs);

        var answer = await new ActivityEnvironment().RunAsync(() => activity.FindLastAttemptCauseAsync(
            new FindLastAttemptCauseInput(acceptance.Outputs[0].OutputId, 0)));

        Assert.NotNull(answer);
        Assert.Equal(3, answer!.Attempt);
        Assert.Equal(AttemptCauseVocabulary.GuardDeadline, answer.Cause);
    }

    [Fact]
    public async Task ABlockThatRecordedNothingAnswersAnEmptyResult()
    {
        var stores = ReportJobFixture.Stores();
        var acceptance = await ReportJobFixture.AcceptedAsync(stores);
        var activity = new AttemptCauseActivities(stores.Jobs);

        var answer = await new ActivityEnvironment().RunAsync(() => activity.FindLastAttemptCauseAsync(
            new FindLastAttemptCauseInput(acceptance.Outputs[0].OutputId, 0)));

        Assert.Null(answer);
    }

    [Fact]
    public async Task AnotherOutputsRowsAreNeverThisBlocks()
    {
        var stores = ReportJobFixture.Stores();
        var one = await ReportJobFixture.AcceptedAsync(stores);
        var other = await ReportJobFixture.AcceptedAsync(stores);
        var cancellationToken = TestContext.Current.CancellationToken;
        await stores.Jobs.RecordAttemptCauseAsync(
            new AttemptCause(other.Outputs[0].OutputId, 0, 1, AttemptCauseVocabulary.WatchdogDeadline, Now),
            cancellationToken);
        var activity = new AttemptCauseActivities(stores.Jobs);

        var answer = await new ActivityEnvironment().RunAsync(() => activity.FindLastAttemptCauseAsync(
            new FindLastAttemptCauseInput(one.Outputs[0].OutputId, 0)));

        Assert.Null(answer);
    }

    [Fact]
    public async Task ABlockNoAttemptOfThisOutputRecordedAnswersAnEmptyResult()
    {
        var stores = ReportJobFixture.Stores();
        var acceptance = await ReportJobFixture.AcceptedAsync(stores);
        var cancellationToken = TestContext.Current.CancellationToken;
        await stores.Jobs.RecordAttemptCauseAsync(
            new AttemptCause(acceptance.Outputs[0].OutputId, 0, 1, AttemptCauseVocabulary.GuardDeadline, Now),
            cancellationToken);
        var activity = new AttemptCauseActivities(stores.Jobs);

        var answer = await new ActivityEnvironment().RunAsync(() => activity.FindLastAttemptCauseAsync(
            new FindLastAttemptCauseInput(acceptance.Outputs[0].OutputId, 1)));

        Assert.Null(answer);
    }
}
