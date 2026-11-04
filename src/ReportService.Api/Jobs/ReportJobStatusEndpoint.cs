using System.Diagnostics;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Telemetry;
using ReportService.Jobs;

namespace ReportService.Api.Jobs;

/// <summary>
/// Reading one report job: the operation behind the address a job creation answered
/// with. It decides in a fixed order — who the request may act as, whether the value
/// could be an identifier at all, and only then what the records say — so a request
/// the service does not serve learns nothing about which jobs exist or how their
/// identifiers are formed. The answer is read in one snapshot of the owner's records,
/// is never cached, and carries the stored condition of a failed job while the read
/// itself succeeds.
/// </summary>
public static class ReportJobStatusEndpoint
{
    /// <summary>The category of the API's job log records.</summary>
    public const string LogCategory = CreateReportJobEndpoint.LogCategory;

    /// <summary>What an answer says when the named job does not exist for the declared consumer.</summary>
    public const string NotFoundTitle = "Report job not found";

    /// <summary>What an answer says when the named job does not exist for the declared consumer.</summary>
    public const string NotFoundDetail = "The report job does not exist.";

    /// <summary>The cache directive of every status answer: an older answer is never served.</summary>
    public const string NoStore = "no-store";

    /// <summary>Adds the job status read to the application.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapReportJobStatusEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(CreateReportJobEndpoint.RoutePrefix + "/{reportJobId}", ReadAsync);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(
        HttpContext context,
        IJobStore store,
        ConsumerRegistry registry,
        ConsumerRefusalMetrics refusals,
        ILoggerFactory loggerFactory,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        // Reading is kept for a consumer that was made inactive: the gate asks only
        // that it is registered, and no record is touched before it answers.
        var gate = ConsumerAccess.Resolve(context.Request, registry.Current, ConsumerRequirement.Registered);
        if (!gate.Allowed)
        {
            return ConsumerAccess.Refuse(context, gate, ConsumerRefusalMetrics.JobStatusOperation, logger, refusals);
        }

        // A value that cannot be an identifier refers to a job that cannot exist: it is
        // answered as a missing one, without a read, so the answer says nothing about
        // which identifiers exist or how they are formed.
        if (!JobIdentifier.IsWellFormed(reportJobId))
        {
            return NotFound(context);
        }

        var status = await store
            .FindStatusAsync(reportJobId, gate.ConsumerId!, cancellationToken)
            .ConfigureAwait(false);
        if (status is null)
        {
            return NotFound(context);
        }

        // The span names the job only after the owner's read found it: a refusal or a
        // missing job never carries an identifier in its telemetry.
        Activity.Current?.SetTag(CreateReportJobEndpoint.JobIdTag, status.ReportJobId);
        logger.LogDebug("Job {ReportJobId} status read", status.ReportJobId);

        // An intermediary that served an older answer would show counters going back.
        context.Response.Headers["Cache-Control"] = NoStore;
        return Results.Ok(ReportJobStatusResponse.From(status));
    }

    // Every unusable job reference answers the one problem of a job that was not found,
    // whether it exists, belongs to another consumer, or cannot be an identifier at all.
    private static IResult NotFound(HttpContext context) =>
        ApiProblem.Create(
            context,
            ErrorCodes.InputNotFound,
            StatusCodes.Status404NotFound,
            NotFoundTitle,
            NotFoundDetail);
}
