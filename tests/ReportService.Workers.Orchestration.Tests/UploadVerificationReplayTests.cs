using ReportService.Workers.Orchestration.Uploads;
using Temporalio.Common;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Tests;

/// <summary>
/// Replays the workflow histories captured against a real engine, so a change that
/// would make a running execution non-deterministic fails here instead of in
/// production. A history file is a fixture: it is generated once, against an engine,
/// by the capture case of the integration suite, and committed with the change that
/// captured it. The fixture is part of what this work delivers, so a directory with
/// no history in it fails the case: nothing was replayed, and a case that had
/// nothing to check is not a result.
/// </summary>
public sealed class UploadVerificationReplayTests
{
    /// <summary>The directory the captured histories are committed in.</summary>
    public const string HistoriesDirectory = "Histories";

    [Fact]
    public async Task EveryCapturedHistoryReplaysWithoutADivergence()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, HistoriesDirectory);
        var histories = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToList()
            : [];

        Assert.True(
            histories.Count > 0,
            $"no recorded verification history exists under {directory}: the fixture is the outcome of a run against a real engine and it is part of this work, so there is nothing to replay.");

        var options = new WorkflowReplayerOptions();
        options.Workflows.Add(WorkflowDefinition.Create(typeof(UploadVerificationWorkflow)));
        var replayer = new WorkflowReplayer(options);
        foreach (var history in histories)
        {
            // The identifier passed beside the history only labels the replay result: what
            // the case proves is that the recorded execution replays without a divergence.
            await replayer.ReplayWorkflowAsync(
                WorkflowHistory.FromJson(
                    Path.GetFileNameWithoutExtension(history),
                    await File.ReadAllTextAsync(history, TestContext.Current.CancellationToken)),
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void TheWorkflowIsRegisteredUnderTheNameTheDispatcherStarts()
    {
        Assert.Equal("UploadVerification", UploadVerificationWorkflow.WorkflowType);
        Assert.Equal("report-orchestration", UploadVerificationWorkflow.OrchestrationQueue);
        Assert.Equal("upload/upload-0123456789abcdef0123456789abcdef", UploadVerificationWorkflow.WorkflowIdFor("upload-0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void TheAttemptOptionsNameTheDataQueueAndTheBoundedPolicy()
    {
        var options = UploadVerificationActivityOptions.Create(TimeSpan.FromHours(1), TimeSpan.FromHours(6));

        Assert.Equal("report-data", UploadVerificationActivityOptions.DataQueue);
        Assert.Equal(UploadVerificationActivityOptions.DataQueue, options.TaskQueue);
        Assert.Equal(5, options.RetryPolicy!.MaximumAttempts);
    }
}
