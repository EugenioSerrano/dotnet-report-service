using ReportService.TemplateSource.Take;
using ReportService.Workers.Pdf.Templates;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Pdf.Tests.Templates;

/// <summary>
/// The take as the queue runs it: the activity answers the values a caller pins a
/// job to, tells the three success states apart, and turns a refusal into the
/// engine's own failure — the take's code as the failure's type, and retried only
/// when the take said the same version may succeed later.
/// </summary>
public sealed class TakeTemplateVersionActivityTests
{
    [Fact]
    public async Task AFirstTakeAnswersTheValuesTheCallerPinsTheJobTo()
    {
        await using var harness = new TakeHarness();
        harness.Seed("invoice-energy", "1.1.0");
        var activity = new TakeTemplateVersionActivity(harness.Take());

        var result = await RunAsync(activity, "invoice-energy", "1.1.0", "billing-system");

        Assert.Equal("invoice-energy", result.TemplateId);
        Assert.Equal("1.1.0", result.Version);
        Assert.Equal("billing-system", result.OwnerConsumerId);
        Assert.False(result.Shared);
        Assert.Equal(harness.Source.CommitSha, result.CommitSha);
        Assert.Equal(64, result.PackageSha256.Length);
        Assert.True(result.PackageSize > 0);
        Assert.Equal("SCRIBAN", result.Engine);
        Assert.Equal("7.5.1", result.EngineVersion);
        Assert.Equal("schema.json", result.SchemaRef);
        Assert.Equal(TakeTemplateVersionOutcomes.Taken, result.Outcome);
    }

    [Fact]
    public async Task ATakenVersionAnswersAlreadyTakenAndOneReadAgainConflictLogged()
    {
        await using var harness = new TakeHarness();
        harness.Seed("invoice-energy", "1.1.0");
        var activity = new TakeTemplateVersionActivity(harness.Take());

        await RunAsync(activity, "invoice-energy", "1.1.0", "billing-system");
        var again = await RunAsync(activity, "invoice-energy", "1.1.0", "billing-system");

        Assert.Equal(TakeTemplateVersionOutcomes.AlreadyTaken, again.Outcome);

        // A take that lost the race to another take of the same version with different
        // bytes binds the copy that stays and says so; the caller is not refused.
        await using var racing = new TakeHarness();
        racing.Seed("invoice-energy", "1.2.0");
        var racingTake = racing.Take(versions: racing.Racing("invoice-energy", "1.2.0", new string('a', 64)));
        var lost = await RunAsync(new TakeTemplateVersionActivity(racingTake), "invoice-energy", "1.2.0", "billing-system");

        Assert.Equal(TakeTemplateVersionOutcomes.ConflictLogged, lost.Outcome);
    }

    [Fact]
    public async Task ARefusalIsRetriedOnlyWhenTheTakeSaidItMaySucceedLater()
    {
        await using var harness = new TakeHarness();
        harness.Seed("invoice-energy", "1.1.0");
        var activity = new TakeTemplateVersionActivity(harness.Take());

        harness.Source.SetUnreachable(true);
        var retryable = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => RunAsync(activity, "invoice-energy", "1.1.0", "billing-system"));

        Assert.Equal(TemplateTakeCodes.TemplateSourceUnavailable, retryable.ErrorType);
        Assert.False(retryable.NonRetryable);
        Assert.Contains("retried later", retryable.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AVersionsOwnRefusalIsNeverRetried()
    {
        await using var harness = new TakeHarness();
        harness.Seed("invoice-energy", "1.1.0", owner: "billing-system");
        var activity = new TakeTemplateVersionActivity(harness.Take());

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => RunAsync(activity, "invoice-energy", "1.1.0", "reporting-portal"));

        Assert.Equal(TemplateTakeCodes.TemplateNotFound, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("not found", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<TakeTemplateVersionResult> RunAsync(
        TakeTemplateVersionActivity activity,
        string templateId,
        string version,
        string consumer) =>
        new ActivityEnvironment().RunAsync(
            () => activity.RunAsync(new TakeTemplateVersionInput(templateId, version, consumer, "job-1")));
}
