using Aspire.Hosting;
using Aspire.Hosting.Testing;
using ReportService.Testing;
using Temporalio.Client;

namespace ReportService.EndToEndTests;

/// <summary>
/// The PDF output's run segments on the real engine. The opt-in recording case runs the
/// stage's multi-block job at the composition's default window and writes the output
/// workflow's history, so the hermetic suite can replay a recorded execution under every
/// later build of the workflow.
/// </summary>
[Trait("Requires", "printing")]
public sealed class PdfOutputContinueAsNewEndToEndTests : IDisposable
{
    /// <summary>The variable that makes the recording case write the history instead of skipping.</summary>
    public const string RecordVariable = "REPORTSERVICE_RECORD_PDF_OUTPUT_HISTORY";

    /// <summary>The workflow engine's resource and endpoint in the composition.</summary>
    private const string EngineResource = "temporal";

    private const string EngineEndpoint = "grpc";

    /// <summary>The namespace the composition's workers use.</summary>
    private const string EngineNamespace = "default";

    private static readonly TimeSpan LiveJobDeadline = TimeSpan.FromMinutes(30);

    private readonly MultiBlockStage _stage = new();

    /// <summary>Where the recorded history is committed, from the repository root.</summary>
    /// <returns>The history's path.</returns>
    public static string RecordedHistoryPath() => Path.Combine(
        RepositoryPaths.Root,
        "tests",
        "Pdf",
        "ReportService.Pdf.Output.Tests",
        "Histories",
        "Pdf",
        "pdf-output-multi-block-window-2.json");

    /// <inheritdoc />
    public void Dispose() => _stage.Dispose();

    [Fact]
    public async Task RecordAMultiBlockOutputHistory()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(RecordVariable) == "1",
            $"Opt-in recording: set {RecordVariable}=1 to write the output workflow's history.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _stage.StartAsync(cancellationToken);
        await _stage.TakeTemplateAsync(composition, cancellationToken);
        var uploadId = await _stage.SeedConfirmingUploadAsync(composition, cancellationToken);
        var reportJobId = await _stage.SubmitAsync(composition, uploadId, cancellationToken);
        await _stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

        var ended = await _stage.WaitForEndAsync(composition, reportJobId, cancellationToken, LiveJobDeadline);
        Assert.Equal("SUCCEEDED", ended.OutputStatus);
        var partition = await _stage.ReadPartitionAsync(composition, reportJobId, cancellationToken);
        Assert.True(partition.BlockCount > 1, $"the recording needs a multi-block output; the partition has {partition.BlockCount} block(s).");

        var client = await ConnectAsync(composition);
        var history = await client.GetWorkflowHandle(OutputWorkflowId(reportJobId)).FetchHistoryAsync();

        var path = RecordedHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, history.ToJson(), cancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"recorded output history: blocks={partition.BlockCount} events={history.Events.Count} path={path}");
    }

    /// <summary>The output workflow's identifier for a job, as the job workflow starts it.</summary>
    /// <param name="reportJobId">The job.</param>
    /// <returns>The identifier.</returns>
    internal static string OutputWorkflowId(string reportJobId) => $"report-job/{reportJobId}/output/pdf";

    /// <summary>Connects a client to the composition's workflow engine.</summary>
    /// <param name="composition">The running composition.</param>
    /// <returns>The client.</returns>
    internal static async Task<TemporalClient> ConnectAsync(MultiBlockStage.Composition composition)
    {
        var endpoint = composition.App.GetEndpoint(EngineResource, EngineEndpoint);
        return await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"{endpoint.Host}:{endpoint.Port}") { Namespace = EngineNamespace });
    }
}
