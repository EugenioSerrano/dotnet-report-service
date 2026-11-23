using System.Diagnostics;
using System.Globalization;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Telemetry;
using ReportService.Api.Uploads;
using ReportService.Jobs;
using ReportService.Storage;

namespace ReportService.Api.Artifacts;

/// <summary>
/// Downloading one published artifact of one report job: the exact verified file
/// the listing described. It decides in the same fixed order as the listing — who
/// the request may act as, whether both values could be identifiers at all, what
/// the owner's records say — and then, before the object store is touched, whether
/// the artifact's recorded expiry has passed: an expired artifact is answered
/// from metadata alone, with nothing read and nothing written. A download that is
/// still in time is streamed through the verifying reader, so a stored object that
/// no longer matches its size or its SHA-256 ends as an incomplete transfer and
/// never as a successful file.
/// </summary>
public static class ArtifactDownloadEndpoint
{
    /// <summary>The category of the API's job log records.</summary>
    public const string LogCategory = ReportJobStatusEndpoint.LogCategory;

    /// <summary>The route's last segment, after the artifact's identifier.</summary>
    public const string RouteSuffix = "/download";

    /// <summary>What an answer says when the named artifact does not exist for the declared consumer.</summary>
    public const string NotFoundTitle = "Artifact not found";

    /// <summary>What an answer says when the named artifact does not exist for the declared consumer.</summary>
    public const string NotFoundDetail = "The artifact does not exist.";

    /// <summary>What an answer says when the named artifact's expiry has passed.</summary>
    public const string ExpiredTitle = "Artifact expired";

    /// <summary>What an answer says when the named artifact's expiry has passed.</summary>
    public const string ExpiredDetail = "The artifact has expired and can no longer be downloaded.";

    /// <summary>The cache directive of a download: a stored file is never served from a cache.</summary>
    public const string NoStore = ArtifactListEndpoint.NoStore;

    /// <summary>The directive that stops a browser from guessing the media type of the content.</summary>
    public const string NoSniff = "nosniff";

    /// <summary>The tag an artifact's identifier is carried on in a span.</summary>
    public const string ArtifactIdTag = "artifact.id";

    /// <summary>Adds the artifact download to the application.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapArtifactDownloadEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(
            CreateReportJobEndpoint.RoutePrefix + "/{reportJobId}" + ArtifactListEndpoint.RouteSuffix
                + "/{artifactId}" + RouteSuffix,
            DownloadAsync);
        return endpoints;
    }

    private static async Task DownloadAsync(
        HttpContext context,
        IJobStore store,
        ConsumerRegistry registry,
        ArtifactContent content,
        ArtifactStreaming streaming,
        ConsumerRefusalMetrics refusals,
        ArtifactDownloadMetrics failures,
        ILoggerFactory loggerFactory,
        TimeProvider clock,
        string reportJobId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        // Downloading is kept for a consumer that was made inactive: the gate asks only that
        // it is registered, and no record is touched before it answers.
        var gate = ConsumerAccess.Resolve(context.Request, registry.Current, ConsumerRequirement.Registered);
        if (!gate.Allowed)
        {
            await ConsumerAccess
                .Refuse(context, gate, ConsumerRefusalMetrics.ArtifactDownloadOperation, logger, refusals)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        // A value that cannot be an identifier names an artifact that cannot exist: it is
        // answered as a missing one, without a read, so the answer says nothing about which
        // identifiers exist or how they are formed.
        if (!JobIdentifier.IsWellFormed(reportJobId) || !ArtifactIdentifier.IsWellFormed(artifactId))
        {
            await Refuse(context, ErrorCodes.InputNotFound, StatusCodes.Status404NotFound, NotFoundTitle, NotFoundDetail)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        var artifact = await store
            .FindArtifactAsync(reportJobId, artifactId, gate.ConsumerId!, cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null)
        {
            await Refuse(context, ErrorCodes.InputNotFound, StatusCodes.Status404NotFound, NotFoundTitle, NotFoundDetail)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        // The expiry is read from the row, never from the stored bytes: an artifact that
        // expired is expired even when its object is still there, and the object store is not
        // touched at all. The clock is read at the precision every stored row carries.
        var now = JobWireValues.TruncateToMillisecond(clock.GetUtcNow());
        if (now >= artifact.ExpiresAt)
        {
            await Refuse(context, ErrorCodes.ArtifactExpired, StatusCodes.Status410Gone, ExpiredTitle, ExpiredDetail)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        var opened = await content.OpenAsync(artifact, cancellationToken).ConfigureAwait(false);
        if (!opened.Succeeded || opened.Value is null)
        {
            // The artifact exists for this consumer and has not expired: a stored object the
            // service cannot read is the service's fault, never the caller's, and never
            // "not found" or "expired".
            failures.MissingObject();
            var status = StatusOfFailure(opened.Outcome);
            LogUnreadable(logger, status, artifact.ArtifactId, opened.Outcome);
            await Refuse(
                    context,
                    ErrorCodes.ServiceUnavailable,
                    status,
                    ApiExceptionHandler.ServiceUnavailableTitle,
                    ApiExceptionHandler.ServiceUnavailableDetail)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        Activity.Current?.SetTag(CreateReportJobEndpoint.JobIdTag, artifact.ReportJobId);
        Activity.Current?.SetTag(ArtifactIdTag, artifact.ArtifactId);

        // The length is declared before the first byte: a client can then tell a complete
        // download from one the service cut, without waiting for a trailer.
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = artifact.MediaType;
        context.Response.ContentLength = artifact.SizeBytes;
        context.Response.Headers.ContentDisposition =
            string.Create(CultureInfo.InvariantCulture, $"attachment; filename=\"{artifact.ReportJobId}.{JobWireValues.ToWire(artifact.Format)}\"");
        context.Response.Headers.CacheControl = NoStore;
        context.Response.Headers["X-Content-Type-Options"] = NoSniff;

        // Both scopes are explicit: the read raises the integrity failure, the write only
        // ever fails because the client left, and neither leaves the object unreleased.
        await using var read = opened.Value;
        var delivered = await streaming
            .CopyVerifiedAsync(context, read.Content, artifact.SizeBytes, artifact.ArtifactId, cancellationToken)
            .ConfigureAwait(false);
        if (delivered)
        {
            logger.LogInformation(
                "Artifact {ArtifactId} of job {ReportJobId} downloaded ({SizeBytes} bytes)",
                artifact.ArtifactId,
                artifact.ReportJobId,
                artifact.SizeBytes);
        }
    }

    // Every unusable reference answers the one problem of an artifact that was not found:
    // whether it exists, belongs to another consumer, belongs to another job or cannot be an
    // identifier at all. The expired answer is a different one, so a consumer that listed
    // first always learns which of the two happened.
    private static IResult Refuse(HttpContext context, string code, int status, string title, string detail)
    {
        Activity.Current?.SetTag(UploadEndpoints.ErrorCodeTag, code);
        return ApiProblem.Create(context, code, status, title, detail);
    }

    // The port's outcomes, mapped one by one: a cause worth retrying is reported as one, and
    // every other failure of a read is the service's own fault. The last arm is what an unnamed
    // value of the port's own enumeration would reach - a fault, never a success.
    private static int StatusOfFailure(StorageOutcome outcome) => outcome switch
    {
        StorageOutcome.Transient => StatusCodes.Status503ServiceUnavailable,
        StorageOutcome.NotFound => StatusCodes.Status500InternalServerError,
        StorageOutcome.Permanent => StatusCodes.Status500InternalServerError,
        StorageOutcome.IntegrityFailure => StatusCodes.Status500InternalServerError,
        StorageOutcome.AlreadyExists => StatusCodes.Status500InternalServerError,
        StorageOutcome.Success => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static void LogUnreadable(ILogger logger, int status, string artifactId, StorageOutcome outcome)
    {
        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            logger.LogWarning(
                "Stored object of artifact {ArtifactId} is missing or unreadable ({Outcome})",
                artifactId,
                outcome);
            return;
        }

        logger.LogError(
            "Stored object of artifact {ArtifactId} is missing or unreadable ({Outcome})",
            artifactId,
            outcome);
    }
}
