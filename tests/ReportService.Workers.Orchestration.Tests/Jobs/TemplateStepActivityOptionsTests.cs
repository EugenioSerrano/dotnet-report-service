using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The execution settings the job's template steps run under, asserted value by value and
/// with no engine: the three metadata steps on the orchestration queue under the unbounded
/// metadata policy, and the two activities on the PDF worker's queue whose settings are the
/// delivered ones - the take's Bolt owns its timeouts and retries, and this worker mirrors
/// them so the two can never disagree about what a take is given.
/// </summary>
public sealed class TemplateStepActivityOptionsTests
{
    [Fact]
    public void TheMetadataStepsRunOnTheOrchestrationQueueUnderTheUnboundedPolicy()
    {
        var options = TemplateStepActivityOptions.Create();

        Assert.Equal("report-orchestration", options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(30), options.StartToCloseTimeout);
        Assert.Null(options.HeartbeatTimeout);
        Assert.NotNull(options.RetryPolicy);
        Assert.Equal(TimeSpan.FromSeconds(1), options.RetryPolicy!.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RetryPolicy.MaximumInterval);
        Assert.Equal(0, options.RetryPolicy.MaximumAttempts);
        Assert.Equal(
            ["TEMPLATE_NOT_FOUND", "TEMPLATE_INVALID", "TEMPLATE_SOURCE_UNAVAILABLE", "UNSUPPORTED_CAPABILITY", "EXECUTION_FAILED"],
            options.RetryPolicy.NonRetryableErrorTypes);
    }

    [Fact]
    public void TheTakeIsScheduledOnThePdfQueueWithTheSettingsTheTakeItselfPublishes()
    {
        var options = TakeTemplateStepOptions.Create();

        Assert.Equal("report-pdf-rendering", options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(300), options.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatTimeout);
        Assert.NotNull(options.RetryPolicy);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryPolicy!.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryPolicy.MaximumInterval);
        Assert.Equal(3, options.RetryPolicy.MaximumAttempts);
        Assert.Equal(["TEMPLATE_NOT_FOUND", "TEMPLATE_INVALID", "UNSUPPORTED_CAPABILITY"], options.RetryPolicy.NonRetryableErrorTypes);
    }

    [Fact]
    public void TheByNameResolutionIsScheduledOnThePdfQueueWithTheSettingsItPublishes()
    {
        var options = ResolveTemplateStepOptions.Create();

        Assert.Equal("report-pdf-rendering", options.TaskQueue);
        Assert.Equal(TimeSpan.FromSeconds(300), options.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatTimeout);
        Assert.NotNull(options.RetryPolicy);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryPolicy!.InitialInterval);
        Assert.Equal(2f, options.RetryPolicy.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryPolicy.MaximumInterval);
        Assert.Equal(3, options.RetryPolicy.MaximumAttempts);
        Assert.Equal(["TEMPLATE_NOT_FOUND", "TEMPLATE_INVALID"], options.RetryPolicy.NonRetryableErrorTypes);
    }

    [Theory]
    [InlineData("TakeTemplateVersion")]
    [InlineData("ResolveTemplateVersion")]
    public void TheActivitiesAreScheduledByTheNamesThePdfWorkerRegisters(string name)
    {
        Assert.Contains(name, new[] { TemplateStepContracts.TakeTemplateVersion, TemplateStepContracts.ResolveTemplateVersion }, StringComparer.Ordinal);
    }

    [Fact]
    public void TheMetadataPolicyIsTheOneTheDeliveredStepsAlreadyRunUnder()
    {
        var metadata = TemplateStepActivityOptions.Create();
        var delivered = ReportJobActivityOptions.Create();

        Assert.Equal(delivered.TaskQueue, metadata.TaskQueue);
        Assert.Equal(delivered.StartToCloseTimeout, metadata.StartToCloseTimeout);
        Assert.Equal(delivered.RetryPolicy!.MaximumAttempts, metadata.RetryPolicy!.MaximumAttempts);
    }

    [Fact]
    public void TheMirroredTakeOutcomesAreTheDeliveredOnes()
    {
        Assert.Equal("TAKEN", TemplateStepContracts.TakeOutcomes.Taken);
        Assert.Equal("ALREADY_TAKEN", TemplateStepContracts.TakeOutcomes.AlreadyTaken);
        Assert.Equal("CONFLICT_LOGGED", TemplateStepContracts.TakeOutcomes.ConflictLogged);
        Assert.Equal("RESOLVED", TemplateStepContracts.ResolutionOutcomes.Resolved);
        Assert.Equal("RESOLVED_FROM_TAKEN_COPY", TemplateStepContracts.ResolutionOutcomes.ResolvedFromTakenCopy);
    }

    [Fact]
    public void TheStepIsGatedBehindItsOwnPatch()
    {
        Assert.Equal("template-resolve-v1", ReportJobWorkflow.TemplateResolvePatch);
        Assert.NotEqual(ReportJobWorkflow.JobStatusPathPatch, ReportJobWorkflow.TemplateResolvePatch);
    }
}
