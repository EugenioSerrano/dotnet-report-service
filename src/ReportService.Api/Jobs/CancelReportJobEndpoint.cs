using System.Diagnostics;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Telemetry;
using ReportService.Jobs;

namespace ReportService.Api.Jobs;

/// <summary>
/// Requesting the cancellation of one report job: the durable entry point that records a
/// consumer's wish to stop a running job and moves it to <c>CANCELLING</c>. It decides in
/// a fixed order — who the request may act as, whether the value could be an identifier at
/// all, and only then what the records say — so a request the service does not serve learns
/// nothing about which jobs exist. The decision never observes the client's connection, so a
/// timeout or a disconnect never cancels a job; a cancel of a still-running job is accepted
/// (<c>202</c>) with its durable intent recorded once; a repeat while cancelling is the same
/// accepted idempotent answer with no second intent; a cancel of an already-terminal job is a
/// no-op that returns its current state (<c>200</c>); and a job of another consumer, absent or
/// malformed, is the one not-found (<c>404</c>). The actual wind-down and the terminal
/// <c>CANCELLED</c> state are US-010.BOLT-002.
/// </summary>
public static class CancelReportJobEndpoint
{
    /// <summary>The category of the API's job log records.</summary>
    public const string LogCategory = CreateReportJobEndpoint.LogCategory;

    /// <summary>What an answer says when the named job does not exist for the declared consumer.</summary>
    public const string NotFoundTitle = ReportJobStatusEndpoint.NotFoundTitle;

    /// <summary>What an answer says when the named job does not exist for the declared consumer.</summary>
    public const string NotFoundDetail = ReportJobStatusEndpoint.NotFoundDetail;

    /// <summary>The cache directive of every cancel answer: an older answer is never served.</summary>
    public const string NoStore = ReportJobStatusEndpoint.NoStore;

    /// <summary>What an answer says when the cancel could not be recorded for a cause on the service's side.</summary>
    public const string ServiceUnavailableTitle = CreateReportJobEndpoint.ServiceUnavailableTitle;

    /// <summary>What an answer says when the cancellation could not be recorded.</summary>
    public const string ServiceUnavailableDetail =
        "The cancellation could not be recorded; the service cannot complete the request now.";

    /// <summary>
    /// The operation label this surface's gate refusals carry. It is a fixed value defined
    /// here rather than in the shared refusal catalogue, to keep this Bolt's change within the
    /// SPEC's authorised file set (SPEC-261126-0252 §6).
    /// </summary>
    public const string CancelOperation = "job-cancel";

    /// <summary>Adds the job cancel operation to the application.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapCancelReportJobEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(CreateReportJobEndpoint.RoutePrefix + "/{reportJobId}/cancel", CancelAsync);
        return endpoints;
    }

    private static async Task<IResult> CancelAsync(
        HttpContext context,
        IJobStore store,
        ConsumerRegistry registry,
        ConsumerRefusalMetrics refusals,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        string reportJobId)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        // Cancelling is stopping your own running job — a safety and cost control — so a
        // registered consumer that was made inactive may still do it: the gate asks only that
        // the consumer is registered, and no record is touched before it answers.
        var gate = ConsumerAccess.Resolve(context.Request, registry.Current, ConsumerRequirement.Registered);
        if (!gate.Allowed)
        {
            return ConsumerAccess.Refuse(context, gate, CancelOperation, logger, refusals);
        }

        // A value that cannot be an identifier refers to a job that cannot exist: it is answered
        // as a missing one, without a read, so the answer says nothing about which identifiers
        // exist or how they are formed.
        if (!JobIdentifier.IsWellFormed(reportJobId))
        {
            return NotFound(context);
        }

        CancellationRequestResult result;
        try
        {
            // The client's connection is never observed: a timeout or a disconnect must never be
            // treated as a cancellation, so the decision runs on CancellationToken.None and an
            // accepted request runs to its commit on its own (AC-6 / R7).
            result = await store
                .RequestCancellationAsync(reportJobId, gate.ConsumerId!, clock.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (JobStoreUnavailableException exception)
        {
            logger.LogWarning("A job cancellation could not be recorded ({ExceptionType})", exception.GetType().Name);
            return ApiProblem.Create(
                context,
                ErrorCodes.ServiceUnavailable,
                StatusCodes.Status503ServiceUnavailable,
                ServiceUnavailableTitle,
                ServiceUnavailableDetail);
        }

        return result switch
        {
            CancellationRequestResult.Transitioned transitioned => Accepted(context, logger, transitioned.View, firstCancel: true),
            CancellationRequestResult.AlreadyCancelling already => Accepted(context, logger, already.View, firstCancel: false),
            CancellationRequestResult.AlreadyTerminal terminal => NoOp(context, logger, terminal.View),
            _ => NotFound(context),
        };
    }

    // An accepted cancel: the transition (or its idempotent replay while cancelling). The span
    // names the job only after the owner's read found it, and the answer is never cached.
    private static IResult Accepted(HttpContext context, ILogger logger, ReportJobStatusView view, bool firstCancel)
    {
        Activity.Current?.SetTag(CreateReportJobEndpoint.JobIdTag, view.ReportJobId);
        logger.LogInformation(
            firstCancel
                ? "Job {ReportJobId} cancellation requested"
                : "Job {ReportJobId} cancellation requested again while cancelling",
            view.ReportJobId);
        context.Response.Headers["Cache-Control"] = NoStore;
        return Results.Accepted(
            $"{CreateReportJobEndpoint.RoutePrefix}/{view.ReportJobId}",
            ReportJobStatusResponse.From(view, logger));
    }

    // A cancel of an already-terminal job: a no-op that returns the unchanged current state.
    private static IResult NoOp(HttpContext context, ILogger logger, ReportJobStatusView view)
    {
        Activity.Current?.SetTag(CreateReportJobEndpoint.JobIdTag, view.ReportJobId);
        logger.LogDebug("Job {ReportJobId} cancellation requested for a terminal job; no-op", view.ReportJobId);
        context.Response.Headers["Cache-Control"] = NoStore;
        return Results.Ok(ReportJobStatusResponse.From(view, logger));
    }

    // Every unusable job reference answers the one problem of a job that was not found, whether
    // it exists for another consumer, does not exist, or cannot be an identifier at all.
    private static IResult NotFound(HttpContext context) =>
        ApiProblem.Create(
            context,
            ErrorCodes.InputNotFound,
            StatusCodes.Status404NotFound,
            NotFoundTitle,
            NotFoundDetail);
}
