using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.Workers.Orchestration.Retention;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What publishing an HTML output is asked to do: the object its <c>.html</c> was assembled into.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the output.</param>
/// <param name="ObjectKey">The key the assembled file was written under.</param>
/// <param name="Sha256">The SHA-256 of the stored object, lower-case hexadecimal.</param>
/// <param name="SizeBytes">The size of the stored object.</param>
public sealed record PublishHtmlOutputInput(
    string ReportJobId,
    string OutputId,
    string ObjectKey,
    string Sha256,
    long SizeBytes);

/// <summary>
/// The publication of one HTML output's assembled file as exactly one artifact, written in the one
/// transaction that also marks the output succeeded. The artifact's identifier is generated here, in
/// the activity, so a retry of the step cannot publish a second one.
/// </summary>
public sealed class HtmlOutputActivities
{
    /// <summary>The media type an HTML artifact is served as.</summary>
    public const string HtmlMediaType = "text/html; charset=utf-8";

    /// <summary>The name the HTML publication is registered and scheduled under.</summary>
    public const string PublishHtmlOutputName = "PublishHtmlOutput";

    /// <summary>
    /// The category the publication logs under: the one it has always logged under, so the queries
    /// and dashboards that read its entries keep finding them.
    /// </summary>
    public const string PublicationLogCategory = "ReportService.Workers.Orchestration.Jobs.PdfOutputActivities";

    /// <summary>The code a state the step cannot use ends with.</summary>
    private const string ExecutionFailed = "EXECUTION_FAILED";

    private readonly IJobStore jobs;
    private readonly TimeProvider clock;
    private readonly IOptions<RetentionOptions> retention;
    private readonly ILoggerFactory loggers;
    private readonly ILogger logger;

    /// <summary>Creates the publication step.</summary>
    /// <param name="jobs">The job store the publication is written to.</param>
    /// <param name="clock">The clock the publication instant is read from.</param>
    /// <param name="retention">How long a published output is kept.</param>
    /// <param name="loggers">The factory the job's log scope and the publication's logger come from.</param>
    public HtmlOutputActivities(
        IJobStore jobs,
        TimeProvider clock,
        IOptions<RetentionOptions> retention,
        ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        this.jobs = jobs;
        this.clock = clock;
        this.retention = retention;
        this.loggers = loggers;
        logger = loggers.CreateLogger(PublicationLogCategory);
    }

    /// <summary>
    /// Publishes one HTML output's assembled <c>.html</c> as its one artifact, the same publication
    /// transaction the PDF output uses (guard + insert-once + mark succeeded), parameterised to the
    /// HTML media type and no page count. An artifact that exists already answers the one that
    /// exists and writes nothing; a guard refusal ends the step without writing anything.
    /// </summary>
    /// <param name="input">The output and the object its <c>.html</c> was assembled into.</param>
    /// <returns>The outcome and the identifier of the artifact that exists.</returns>
    [Activity(PublishHtmlOutputName)]
    public async Task<PublishOutputResult> PublishHtmlOutputAsync(PublishHtmlOutputInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);
        System.Diagnostics.Activity.Current?.SetTag("output.id", input.OutputId);

        var now = JobWireValues.TruncateToMillisecond(clock.GetUtcNow());
        var artifact = new ArtifactRecord(
            NewArtifactId(),
            input.ReportJobId,
            input.OutputId,
            OutputFormat.Html,
            HtmlMediaType,
            input.ObjectKey,
            input.Sha256,
            input.SizeBytes,
            null,
            now,
            now + retention.Value.Outputs);

        var published = await jobs
            .PublishAsync(artifact, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        switch (published.Outcome)
        {
            case PublishOutcome.Published:
                logger.LogInformation(
                    "HTML output {OutputId} published as {ArtifactId}",
                    input.OutputId,
                    published.ArtifactId);
                return new PublishOutputResult(PublishOutputResult.Published, published.ArtifactId!);

            case PublishOutcome.AlreadyPublished when published.ArtifactId is not null:
                logger.LogInformation(
                    "HTML output {OutputId} was published already as {ArtifactId}",
                    input.OutputId,
                    published.ArtifactId);
                return new PublishOutputResult(PublishOutputResult.AlreadyPublished, published.ArtifactId);

            default:
                logger.LogWarning(
                    "HTML output {OutputId} could not be published: the output is no longer running",
                    input.OutputId);
                throw Refuse("the output is no longer running");
        }
    }

    private static string NewArtifactId() => OutputArtifacts.NewArtifactId();

    private static ApplicationFailureException Refuse(string reason) =>
        new($"{ExecutionFailed}: {reason}", errorType: ExecutionFailed, nonRetryable: true);
}
