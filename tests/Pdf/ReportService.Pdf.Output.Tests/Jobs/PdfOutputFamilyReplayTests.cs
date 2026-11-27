using ReportService.Testing;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Common;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The recorded job runs that reach the PDF output family, replayed against the job workflow with
/// the families registered as the worker registers them: the single-PDF path of a run recorded before
/// jobs had several outputs, and the multi-output path with the PDF output started, refused or absent.
/// A run whose commands the family no longer reproduces fails here.
/// </summary>
[Collection(JobWorkflowSettings.Name)]
public sealed class PdfOutputFamilyReplayTests
{
    /// <summary>The recordings whose runs go through the PDF family.</summary>
    public static TheoryData<string> Recordings { get; } = new(
        JobHistoryFixtures.RowBoundRefusedFile,
        JobHistoryFixtures.MultiOutputPriorityFile,
        JobHistoryFixtures.HtmlOnlyFile,
        JobHistoryFixtures.MultiOutputPdfRefusedFile);

    [Theory]
    [MemberData(nameof(Recordings))]
    public async Task ARunThroughThePdfFamilyReplays(string file)
    {
        var fixture = JobHistoryFixtures.ByFile(file);
        ReportJobWorkflow.Configure(fixture.Backstop, fixture.ContinueAsNewAfter);
        OutputFamilies.Use(OrchestrationWorkerRegistration.Families());

        var options = new WorkflowReplayerOptions();
        options.Workflows.Add(WorkflowDefinition.Create(typeof(ReportJobWorkflow)));
        var replayer = new WorkflowReplayer(options);

        var path = Path.Combine(AppContext.BaseDirectory, "Histories", "Jobs", file);
        Assert.True(File.Exists(path), $"the recorded job history {file} is missing from {Path.GetDirectoryName(path)}.");
        await replayer.ReplayWorkflowAsync(
            WorkflowHistory.FromJson(
                Path.GetFileNameWithoutExtension(file),
                await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)),
            cancellationToken: TestContext.Current.CancellationToken);
    }
}
