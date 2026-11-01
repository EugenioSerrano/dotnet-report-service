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
/// captured it. With no history committed there is nothing to replay, and the case
/// says so instead of pretending to have checked something.
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

        if (histories.Count == 0)
        {
            Assert.Skip("no workflow history has been captured yet; the fixture is generated once against a real engine.");
        }

        var options = new WorkflowReplayerOptions();
        options.Workflows.Add(WorkflowDefinition.Create(typeof(UploadVerificationWorkflow)));
        var replayer = new WorkflowReplayer(options);
        foreach (var history in histories)
        {
            await replayer.ReplayWorkflowAsync(
                WorkflowHistory.FromJson(
                    await File.ReadAllTextAsync(history, TestContext.Current.CancellationToken),
                    "a captured verification history"),
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
        var options = UploadVerificationActivityOptions.Create(TimeSpan.FromHours(1));

        Assert.Equal("report-data", UploadVerificationActivityOptions.DataQueue);
        Assert.Equal(UploadVerificationActivityOptions.DataQueue, options.TaskQueue);
        Assert.Equal(5, options.RetryPolicy!.MaximumAttempts);
    }
}
