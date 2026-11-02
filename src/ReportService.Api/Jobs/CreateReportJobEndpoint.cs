using System.Diagnostics;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Telemetry;
using ReportService.Api.Uploads;
using ReportService.Jobs;
using ReportService.Uploads;

namespace ReportService.Api.Jobs;

/// <summary>
/// Creating a report job: the promise that an accepted job is durable and is
/// started exactly once. The operation decides in a fixed order — who the request
/// may act as, then what it asked for, then whether this increment can serve it —
/// and only then does it look at the data the job would report on, inside the
/// transaction that stores the job. The answer follows that transaction, never
/// precedes it, and nothing after the body has been read observes the client's
/// connection: a consumer that leaves after sending its request has still created
/// its job.
/// </summary>
public static class CreateReportJobEndpoint
{
    /// <summary>The route the job operations are served under.</summary>
    public const string RoutePrefix = "/api/v1/report-jobs";

    /// <summary>The category of the API's job log records.</summary>
    public const string LogCategory = "ReportService.Api.Jobs";

    /// <summary>The tag a job's identifier is carried on in a span.</summary>
    public const string JobIdTag = "job.id";

    /// <summary>The only output format this increment produces.</summary>
    public const string SupportedOutput = "pdf";

    /// <summary>What an answer says when the request asks for an output this increment does not produce.</summary>
    public const string UnsupportedOutputDetail =
        "The requested output format is not supported. Supported formats: " + SupportedOutput + ".";

    /// <summary>What an answer says when the request declares a profile this increment does not admit.</summary>
    public const string UnsupportedProfileDetail =
        "The declared data profile is not supported. Supported profiles: " + UploadWireValues.HeaderItemsV1 + ".";

    /// <summary>What an answer says when a registered consumer is inactive and may not create a job.</summary>
    public const string ConsumerInactiveDetail = "The consumer is not active and cannot create jobs.";

    /// <summary>What an answer says when a job could not be accepted for a cause on the service's side.</summary>
    public const string ServiceUnavailableTitle = "Service unavailable";

    /// <summary>What an answer says when the job records could not be reached.</summary>
    public const string ServiceUnavailableDetail =
        "The job could not be accepted; the service cannot complete the request now.";

    /// <summary>Adds the job operations to the application.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapCreateReportJobEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(RoutePrefix, CreateAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        IJobStore store,
        ConsumerRegistry registry,
        JobAcceptanceMetrics metrics,
        TimeProvider clock,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        // Creating a job is the one operation an inactive consumer may not perform, and the
        // declaration is decided before the body is read and before any record is touched.
        var gate = ConsumerAccess.Resolve(context.Request, registry.Current, ConsumerRequirement.Active);
        if (!gate.Allowed)
        {
            return RefuseConsumer(context, gate, metrics, logger);
        }

        var consumerId = gate.ConsumerId!;
        var read = await CreateReportJobRequestReader.ReadAsync(context.Request, context.RequestAborted)
            .ConfigureAwait(false);
        switch (read)
        {
            case CreateReportJobRequestReader.Result.TooLarge:
                return Refuse(
                    context,
                    metrics,
                    logger,
                    ErrorCodes.QuotaExceeded,
                    StatusCodes.Status413PayloadTooLarge,
                    "Request too large",
                    UploadEndpoints.BodyTooLargeDetail,
                    RefusalReasons.BodySize);

            case CreateReportJobRequestReader.Result.Rejected rejected:
                return Refuse(
                    context,
                    metrics,
                    logger,
                    ErrorCodes.RequestInvalid,
                    StatusCodes.Status400BadRequest,
                    RequestInvalidDetails.Title,
                    rejected.Problem.Detail,
                    rejected.Problem.Reason,
                    rejected.Problem.Extensions);

            case CreateReportJobRequestReader.Result.Accepted accepted:
                var unsupported = UnsupportedDetail(accepted.Request);
                if (unsupported is { } capability)
                {
                    return Refuse(
                        context,
                        metrics,
                        logger,
                        ErrorCodes.UnsupportedCapability,
                        StatusCodes.Status422UnprocessableEntity,
                        "Unsupported capability",
                        capability.Detail,
                        capability.Reason);
                }

                return await AcceptedAsync(
                    context,
                    store,
                    metrics,
                    clock,
                    logger,
                    consumerId,
                    accepted.Request).ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"unhandled read result {read.GetType().Name}");
        }
    }

    private static async Task<IResult> AcceptedAsync(
        HttpContext context,
        IJobStore store,
        JobAcceptanceMetrics metrics,
        TimeProvider clock,
        ILogger logger,
        string consumerId,
        CreateReportJobRequest request)
    {
        // A value that cannot be an identifier refers to an upload that cannot exist: it is
        // answered as a missing one, without a read.
        if (!UploadIdentifier.IsWellFormed(request.UploadId))
        {
            return NotFound(context, metrics, logger);
        }

        var now = JobWireValues.TruncateToMillisecond(clock.GetUtcNow());
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            consumerId,
            request.UploadId,
            request.TemplateId,
            request.TemplateVersion is null ? TemplateReferenceForm.ByName : TemplateReferenceForm.Exact,
            request.TemplateVersion);

        JobAcceptanceResult result;
        try
        {
            // From here the client's connection is not observed: an acceptance that has begun
            // runs to its commit or its failure on its own, so a disconnect never cancels a job.
            result = await store.AcceptAsync(acceptance, now, CancellationToken.None).ConfigureAwait(false);
        }
        catch (JobStoreUnavailableException exception)
        {
            logger.LogWarning("A job acceptance failed ({ExceptionType})", exception.GetType().Name);
            return Refuse(
                context,
                metrics,
                logger,
                ErrorCodes.ServiceUnavailable,
                StatusCodes.Status503ServiceUnavailable,
                ServiceUnavailableTitle,
                ServiceUnavailableDetail,
                RefusalReasons.StoreUnavailable);
        }

        if (result is JobAcceptanceResult.UploadUnusable)
        {
            return NotFound(context, metrics, logger);
        }

        var job = ((JobAcceptanceResult.Accepted)result).Job;
        var statusUrl = $"{RoutePrefix}/{job.ReportJobId}";
        Activity.Current?.SetTag(JobIdTag, job.ReportJobId);
        metrics.Accepted();
        logger.LogInformation(
            "Job {ReportJobId} accepted for upload {UploadId} ({ReferenceForm})",
            job.ReportJobId,
            job.UploadId,
            ProvisionalTemplateReferenceSyntax.FormOf(request.TemplateVersion));
        return Results.Accepted(statusUrl, ReportJobAcceptedResponse.From(job, statusUrl));
    }

    // Null when the request asks for what this increment offers; otherwise the one refusal and its log reason.
    private static (string Detail, string Reason)? UnsupportedDetail(CreateReportJobRequest request)
    {
        var unsupportedOutput = request.Outputs.Any(output => !string.Equals(output, SupportedOutput, StringComparison.Ordinal));
        var unsupportedProfile = !string.Equals(request.Profile, UploadWireValues.HeaderItemsV1, StringComparison.Ordinal);
        return (unsupportedOutput, unsupportedProfile) switch
        {
            (true, true) => (UnsupportedOutputDetail + " " + UnsupportedProfileDetail, RefusalReasons.OutputAndProfile),
            (true, false) => (UnsupportedOutputDetail, RefusalReasons.Output),
            (false, true) => (UnsupportedProfileDetail, RefusalReasons.Profile),
            _ => null,
        };
    }

    // Every unusable data reference answers the one problem of an upload that was not found,
    // whichever state it is in and whether it exists at all.
    private static IResult NotFound(HttpContext context, JobAcceptanceMetrics metrics, ILogger logger) =>
        Refuse(
            context,
            metrics,
            logger,
            ErrorCodes.InputNotFound,
            StatusCodes.Status404NotFound,
            UploadEndpoints.NotFoundTitle,
            UploadEndpoints.UploadNotFoundDetail,
            RefusalReasons.NotFound);

    private static IResult RefuseConsumer(
        HttpContext context,
        ConsumerAccessResult gate,
        JobAcceptanceMetrics metrics,
        ILogger logger)
    {
        var state = gate.IdentityStateText!;
        if (gate.Inactive)
        {
            logger.LogInformation(
                "Job request refused with {ErrorCode} for consumer {ConsumerId}",
                ErrorCodes.ConsumerInactive,
                gate.ConsumerId);
            return Answer(
                context,
                metrics,
                ErrorCodes.ConsumerInactive,
                StatusCodes.Status403Forbidden,
                ConsumerAccess.InactiveTitle,
                ConsumerInactiveDetail);
        }

        logger.LogInformation(
            "Job request refused with {ErrorCode} ({IdentityState})",
            ErrorCodes.ConsumerUnknown,
            state);
        return Answer(
            context,
            metrics,
            ErrorCodes.ConsumerUnknown,
            StatusCodes.Status403Forbidden,
            ConsumerAccess.UnknownTitle,
            UploadEndpoints.ConsumerUnknownDetail);
    }

    private static IResult Refuse(
        HttpContext context,
        JobAcceptanceMetrics metrics,
        ILogger logger,
        string code,
        int status,
        string title,
        string detail,
        string reason,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        logger.LogInformation("Job request refused with {ErrorCode} ({Reason})", code, reason);
        return Answer(context, metrics, code, status, title, detail, extensions);
    }

    // The one answer of a refusal: the span's code, the counter and the problem document.
    private static IResult Answer(
        HttpContext context,
        JobAcceptanceMetrics metrics,
        string code,
        int status,
        string title,
        string detail,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        Activity.Current?.SetTag(UploadEndpoints.ErrorCodeTag, code);
        metrics.Refusal(code);
        return ApiProblem.Create(context, code, status, title, detail, extensions);
    }

    // The refusal reasons written to the log: constants, never a value from the request.
    private static class RefusalReasons
    {
        public const string BodySize = "body-size";

        public const string Output = "outputs";

        public const string Profile = "profile";

        public const string OutputAndProfile = "outputs,profile";

        public const string NotFound = "not-found";

        public const string StoreUnavailable = "store-unavailable";
    }
}
