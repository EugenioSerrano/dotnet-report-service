using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The three metadata steps that fix a job's template, exercised in the engine's own host
/// for one attempt: a version already taken and usable is bound with one read and no
/// source, one this consumer may not use is refused exactly like an absent one, a version
/// that is not taken asks the workflow for the take, a by-name reference asks for the
/// resolution, and the baseline recorded at acceptance is what an unreachable source falls
/// back to - never a version taken later. The pin is written once: a second attempt, a
/// newer version and a job that ended all leave the record where the first write put it.
/// </summary>
public sealed class TemplateResolutionActivitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    private const string Template = "invoice-energy";

    [Fact]
    public async Task ATakenVersionThisConsumerMayUseIsBoundWithoutASourceRead()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.1.0");

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.Pinned, result.Outcome);
        Assert.Equal(TemplatePinPaths.Taken, result.Path);
        Assert.Equal(1, rig.Versions.FindCalls);
        Assert.Equal("1.1.0", rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin!.ResolvedVersion);
    }

    [Fact]
    public async Task ABindingCompletesTheJobsPresentationFromThePackageItTakes()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.1.0", localeCulture: "es-AR", localeTimeZone: "America/Argentina/Buenos_Aires");

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.Pinned, result.Outcome);
        var state = await rig.Jobs.FindForWorkflowAsync(rig.ReportJobId, TestContext.Current.CancellationToken);
        Assert.Equal("es-AR", state!.Job.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", state.Job.TimeZone);
    }

    [Fact]
    public async Task ATakenVersionAnotherConsumerOwnsAndDoesNotShareIsRefusedAsNotFound()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.1.0", owner: "another-consumer");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PrepareAsync());

        Assert.Equal("TEMPLATE_NOT_FOUND", failure.ErrorType);
        Assert.Equal(TemplateFailureCodes.NotFoundMessage, failure.Message);
        Assert.True(failure.NonRetryable);
        Assert.Contains(rig.Logs.Lines, line => line.Contains("template-not-found", StringComparison.Ordinal));
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task AnUntakenExactVersionAsksForTheTakeWithTheJobsConsumer()
    {
        using var rig = await Rig.CreateAsync("1.0.0");
        rig.Take("1.1.0");

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.NeedsTake, result.Outcome);
        Assert.Equal(Template, result.TemplateId);
        Assert.Equal("1.0.0", result.RequestedVersion);
        Assert.Equal(Rig.Consumer, result.RequestingConsumerId);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task AReferenceByNameAsksForTheResolutionWithTheJobsConsumer()
    {
        using var rig = await Rig.CreateAsync(null);

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.NeedsByName, result.Outcome);
        Assert.Equal(Template, result.TemplateId);
        Assert.Equal(Rig.Consumer, result.RequestingConsumerId);
    }

    [Fact]
    public async Task AJobThatAlreadyEndedIsTerminal()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        await rig.FailJobAsync();

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.Terminal, result.Outcome);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task APinWrittenByAnEarlierAttemptIsTheOneTheJobKeeps()
    {
        using var rig = await Rig.CreateAsync(null);
        rig.Take("1.1.0");
        await rig.PinAsync("1.1.0", taken: false);
        rig.Take("1.2.0");

        var second = await rig.PinAsync("1.2.0", taken: false);

        Assert.Equal(PrepareTemplateOutcome.Pinned, second.Outcome);
        Assert.Equal("1.1.0", rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin!.ResolvedVersion);
    }

    [Fact]
    public async Task APinCompletesAJobThatNamedNoPresentationWithThePackagesOwn()
    {
        using var rig = await Rig.CreateAsync(null);

        var result = await rig.PinAsync(
            "1.1.0",
            taken: true,
            localeCulture: "es-AR",
            localeTimeZone: "America/Argentina/Buenos_Aires");

        Assert.Equal(PrepareTemplateOutcome.Pinned, result.Outcome);
        var state = await rig.Jobs.FindForWorkflowAsync(rig.ReportJobId, TestContext.Current.CancellationToken);
        Assert.Equal("es-AR", state!.Job.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", state.Job.TimeZone);
    }

    [Fact]
    public async Task APinNeverReplacesThePresentationTheRequestNamed()
    {
        using var rig = await Rig.CreateAsync(null, culture: "en-us", timeZone: "Asia/Tokyo");

        await rig.PinAsync(
            "1.1.0",
            taken: true,
            localeCulture: "es-AR",
            localeTimeZone: "America/Argentina/Buenos_Aires");

        var state = await rig.Jobs.FindForWorkflowAsync(rig.ReportJobId, TestContext.Current.CancellationToken);
        Assert.Equal("en-us", state!.Job.Culture);
        Assert.Equal("Asia/Tokyo", state.Job.TimeZone);
    }

    [Fact]
    public async Task AnExactReferenceIsNeverPinnedToAnotherVersion()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.2.0");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PinAsync("1.2.0", taken: false));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task APinPresentOnTheRecordIsAnsweredWithoutReadingAnyVersion()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.1.0");
        await rig.PrepareAsync();
        var reads = rig.Versions.FindCalls;

        var result = await rig.PrepareAsync();

        Assert.Equal(PrepareTemplateOutcome.Pinned, result.Outcome);
        Assert.Equal(TemplatePinPaths.Taken, result.Path);
        Assert.Equal(reads, rig.Versions.FindCalls);
    }

    [Fact]
    public async Task AWrittenPinOnAJobThatEndedMeanwhileIsTerminal()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        await rig.FailJobAsync();

        var result = await rig.PinAsync("1.1.0", taken: true);

        Assert.Equal(PrepareTemplateOutcome.Terminal, result.Outcome);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task APinOfAWorkersVersionForAJobOfAnotherConsumerIsRefused()
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        rig.Take("1.2.0", owner: "another-consumer");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PinAsync("1.2.0", taken: false));

        Assert.Equal("TEMPLATE_NOT_FOUND", failure.ErrorType);
    }

    [Fact]
    public async Task APinOfAVersionNoRecordAnswersIsAnExecutionFailure()
    {
        using var rig = await Rig.CreateAsync("1.1.0");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PinAsync("7.7.7", taken: false));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.Equal(TemplateFailureCodes.RecordMissingMessage, failure.Message);
    }

    [Fact]
    public async Task AnUnreachableSourcePinsTheBaselineRecordedAtAcceptance()
    {
        using var rig = await Rig.CreateAsync(null);
        rig.SetBaseline("1.0.0");
        rig.Versions.Seed(TemplateVersionRecords.Sample(templateId: Template, version: "1.0.0", owner: Rig.Consumer));
        rig.Versions.Seed(TemplateVersionRecords.Sample(templateId: Template, version: "1.10.0", owner: Rig.Consumer));

        var result = await rig.PinBaselineAsync();

        Assert.Equal(PrepareTemplateOutcome.Pinned, result.Outcome);
        Assert.Equal(TemplatePinPaths.Baseline, result.Path);
        Assert.Equal("1.0.0", rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin!.ResolvedVersion);
        Assert.Contains(rig.Logs.Lines, line => line.Contains("taken when the job was accepted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreachableSourceWithoutABaselineFailsTheStepWithItsCode()
    {
        using var rig = await Rig.CreateAsync(null);
        rig.SetBaseline(null);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PinBaselineAsync());

        Assert.Equal("TEMPLATE_SOURCE_UNAVAILABLE", failure.ErrorType);
        Assert.Equal(TemplateFailureCodes.SourceUnavailableMessage, failure.Message);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }

    [Fact]
    public async Task ABaselineWhoseRecordIsGoneIsAnExecutionFailureAndNeverAnotherVersion()
    {
        using var rig = await Rig.CreateAsync(null);
        rig.SetBaseline("4.0.0");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => rig.PinBaselineAsync());

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.Null(rig.Jobs.ReferenceOf(rig.ReportJobId)!.Pin);
    }


}
