using System.Diagnostics;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Telemetry;
using ReportService.Jobs;

namespace ReportService.Api.Artifacts;

/// <summary>
/// Listing one report job's published artifacts: the read a consumer makes before
/// it decides what to download. It decides in a fixed order — who the request may
/// act as, whether the value could be an identifier at all, and only then what the
/// owner's records say — so a request the service does not serve learns nothing
/// about which jobs exist or how their identifiers are formed. The listing is one
/// read of the job and its artifacts, is never cached, and is empty while the job
/// has published nothing.
/// </summary>
public static class ArtifactListEndpoint
{
    /// <summary>The category of the API's job log records.</summary>
    public const string LogCategory = ReportJobStatusEndpoint.LogCategory;

    /// <summary>The cache directive of the listing: an older answer is never served.</summary>
    public const string NoStore = "no-store";

    /// <summary>The route's last segment, after the job's identifier.</summary>
    public const string RouteSuffix = "/artifacts";

    /// <summary>Adds the artifact listing to the application.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapArtifactListEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(CreateReportJobEndpoint.RoutePrefix + "/{reportJobId}" + RouteSuffix, ListAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        IJobStore store,
        ConsumerRegistry registry,
        ConsumerRefusalMetrics refusals,
        ILoggerFactory loggerFactory,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        // Listing is kept for a consumer that was made inactive: the gate asks only
        // that it is registered, and no record is touched before it answers.
        var gate = ConsumerAccess.Resolve(context.Request, registry.Current, ConsumerRequirement.Registered);
        if (!gate.Allowed)
        {
            return ConsumerAccess.Refuse(context, gate, ConsumerRefusalMetrics.ArtifactListOperation, logger, refusals);
        }

        // A value that cannot be an identifier refers to a job that cannot exist: it is
        // answered as a missing one, without a read, so the answer says nothing about
        // which identifiers exist or how they are formed.
        if (!JobIdentifier.IsWellFormed(reportJobId))
        {
            return NotFound(context);
        }

        var artifacts = await store
            .ListArtifactsAsync(reportJobId, gate.ConsumerId!, cancellationToken)
            .ConfigureAwait(false);
        if (artifacts is null)
        {
            return NotFound(context);
        }

        // The span names the job only after the owner's read found it: a refusal or a
        // missing job never carries an identifier in its telemetry.
        Activity.Current?.SetTag(CreateReportJobEndpoint.JobIdTag, artifacts.ReportJobId);
        logger.LogDebug("Artifacts of job {ReportJobId} listed", artifacts.ReportJobId);

        // The answer is a consumer's own file listing; an intermediary that kept it
        // would serve a file list, and an expiry, that are no longer true.
        context.Response.Headers["Cache-Control"] = NoStore;
        return Results.Ok(ArtifactListResponse.From(artifacts));
    }

    // Every unusable job reference answers the one problem of a job that was not found,
    // whether it exists, belongs to another consumer, or cannot be an identifier at all —
    // the answer the job's status read gives for the same identifier, down to its wording.
    private static IResult NotFound(HttpContext context) =>
        ApiProblem.Create(
            context,
            ErrorCodes.InputNotFound,
            StatusCodes.Status404NotFound,
            ReportJobStatusEndpoint.NotFoundTitle,
            ReportJobStatusEndpoint.NotFoundDetail);
}
