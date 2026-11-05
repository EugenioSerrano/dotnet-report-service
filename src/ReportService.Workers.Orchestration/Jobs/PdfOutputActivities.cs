using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What looking up a job's PDF output is asked to do.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record FindPdfOutputInput(string ReportJobId);

/// <summary>The one PDF output a job asked for.</summary>
/// <param name="OutputId">The identifier of the output.</param>
public sealed record FindPdfOutputResult(string OutputId);

/// <summary>What publishing an output is asked to do: the object its block was printed into.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the output.</param>
/// <param name="PartKey">The key the printed block was written under.</param>
/// <param name="Sha256">The SHA-256 of the stored object, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the stored object.</param>
/// <param name="PageCount">How many pages the printed block holds.</param>
public sealed record PublishOutputInput(
    string ReportJobId,
    string OutputId,
    string PartKey,
    string Sha256,
    long SizeBytes,
    int PageCount);

/// <summary>What the publication did, with the artifact that now exists.</summary>
/// <param name="Outcome">Either <c>PUBLISHED</c> or <c>ALREADY_PUBLISHED</c>.</param>
/// <param name="ArtifactId">The identifier of the artifact that exists.</param>
public sealed record PublishOutputResult(string Outcome, string ArtifactId)
{
    /// <summary>The outcome of a publication this call wrote.</summary>
    public const string Published = "PUBLISHED";

    /// <summary>The outcome of a publication an earlier call wrote; nothing was written.</summary>
    public const string AlreadyPublished = "ALREADY_PUBLISHED";
}

/// <summary>
/// The two metadata steps of a job's publication: the lookup of the one PDF output the
/// job asked for, which the workflow needs before it starts the output's own execution —
/// a workflow reads nothing itself — and the publication of the verified block as exactly
/// one artifact, written in the one transaction that also marks the output succeeded. The
/// artifact's identifier is generated here, in the activity, so a retry of the step cannot
/// publish a second one: the store's guard and its insert-once row decide, and a refusal
/// of the guard ends the step without writing anything.
/// </summary>
public sealed class PdfOutputActivities(
    IJobStore jobs,
    TimeProvider clock,
    ILoggerFactory loggers,
    ILogger<PdfOutputActivities> logger)
{
    /// <summary>The name the output lookup is registered and scheduled under.</summary>
    public const string FindPdfOutputName = "FindPdfOutput";

    /// <summary>The name the publication is registered and scheduled under.</summary>
    public const string PublishOutputName = "PublishOutput";

    /// <summary>The media type a PDF artifact is served as.</summary>
    public const string PdfMediaType = "application/pdf";

    /// <summary>The prefix of every artifact identifier.</summary>
    public const string ArtifactPrefix = "artifact-";

    /// <summary>The code a state the step cannot use ends with.</summary>
    private const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>Finds the one PDF output the job asked for.</summary>
    /// <param name="input">The job whose output is looked up.</param>
    /// <returns>The output's identifier.</returns>
    [Activity(FindPdfOutputName)]
    public async Task<FindPdfOutputResult> FindPdfOutputAsync(FindPdfOutputInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);

        var state = await jobs
            .FindForWorkflowAsync(input.ReportJobId, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false)
            ?? throw Refuse("the job is not recorded");

        var pdf = state.Outputs.Where(output => output.Format == OutputFormat.Pdf).ToList();
        if (pdf.Count != 1)
        {
            throw Refuse("the job does not ask for exactly one PDF output");
        }

        return new FindPdfOutputResult(pdf[0].OutputId);
    }

    /// <summary>Publishes one output's verified block as its one artifact.</summary>
    /// <param name="input">The output and the object its block was printed into.</param>
    /// <returns>The outcome and the identifier of the artifact that exists.</returns>
    [Activity(PublishOutputName)]
    public async Task<PublishOutputResult> PublishOutputAsync(PublishOutputInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);
        System.Diagnostics.Activity.Current?.SetTag("output.id", input.OutputId);
        System.Diagnostics.Activity.Current?.SetTag("block.index", 0);

        var artifact = new ArtifactRecord(
            NewArtifactId(),
            input.ReportJobId,
            input.OutputId,
            OutputFormat.Pdf,
            PdfMediaType,
            input.PartKey,
            input.Sha256,
            input.SizeBytes,
            input.PageCount,
            clock.GetUtcNow());

        var published = await jobs
            .PublishAsync(artifact, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        switch (published.Outcome)
        {
            case PublishOutcome.Published:
                logger.LogInformation(
                    "Output {OutputId} published as {ArtifactId}",
                    input.OutputId,
                    published.ArtifactId);
                return new PublishOutputResult(PublishOutputResult.Published, published.ArtifactId!);

            case PublishOutcome.AlreadyPublished when published.ArtifactId is not null:
                logger.LogInformation(
                    "Output {OutputId} was published already as {ArtifactId}",
                    input.OutputId,
                    published.ArtifactId);
                return new PublishOutputResult(PublishOutputResult.AlreadyPublished, published.ArtifactId);

            default:
                logger.LogWarning(
                    "Output {OutputId} could not be published: the output is no longer running",
                    input.OutputId);
                throw Refuse("the output is no longer running");
        }
    }

    /// <summary>Creates an artifact identifier: its prefix and 128 random bits, lower-case hexadecimal.</summary>
    /// <returns>The identifier.</returns>
    public static string NewArtifactId() =>
        ArtifactPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static ApplicationFailureException Refuse(string reason) =>
        new($"{ExecutionFailed}: {reason}", errorType: ExecutionFailed, nonRetryable: true);
}
