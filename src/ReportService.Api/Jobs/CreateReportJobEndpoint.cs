using System.Diagnostics;
using Microsoft.Extensions.Options;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Telemetry;
using ReportService.Api.Uploads;
using ReportService.Jobs;
using ReportService.TemplatePackages.Records;
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

    /// <summary>The output formats this increment produces, in the order the capability message names them.</summary>
    public static readonly IReadOnlyList<string> SupportedOutputs = ["html", "pdf"];

    /// <summary>What an answer says when the request asks for an output this increment does not produce.</summary>
    public static readonly string UnsupportedOutputDetail =
        "The requested output format is not supported. Supported formats: " + string.Join(", ", SupportedOutputs) + ".";

    /// <summary>What an answer says when the request declares a profile this increment does not admit.</summary>
    public const string UnsupportedProfileDetail =
        "The declared data profile is not supported. Supported profiles: " + UploadWireValues.HeaderItemsV1 + ".";

    /// <summary>What an answer says when a registered consumer is inactive and may not create a job.</summary>
    public const string ConsumerInactiveDetail = "The consumer is not active and cannot create jobs.";

    /// <summary>What an answer says when a job could not be accepted for a cause on the service's side.</summary>
    public const string ServiceUnavailableTitle = "Service unavailable";

    /// <summary>
    /// What an answer says when the deployment does not offer the <c>high</c> priority to the
    /// declared consumer. It names the level and nothing of the policy that refused it.
    /// </summary>
    public const string HighPriorityNotOfferedDetail = "The requested priority 'high' is not offered for this request.";

    /// <summary>What an answer says when a key was already used with a different request.</summary>
    public const string ConflictTitle = "Idempotency key conflict";

    /// <summary>What an answer says about a reused key. It never names the key or the first job.</summary>
    public const string ConflictDetail = "The idempotency key was already used with a different request.";

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
        ITemplateVersionStore versions,
        ConsumerRegistry registry,
        JobAcceptanceMetrics metrics,
        IOptions<HighPriorityPolicy> priorityPolicy,
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

        // The key's form is decided before the body is read and before any record is touched:
        // it depends on the request alone, and a request that repeats an accepted one always
        // passes it, exactly as it did the first time.
        var header = IdempotencyKeyHeader.Read(context.Request);
        if (header is IdempotencyKeyHeader.Result.Invalid invalid)
        {
            return Refuse(
                context,
                metrics,
                logger,
                ErrorCodes.RequestInvalid,
                StatusCodes.Status400BadRequest,
                RequestInvalidDetails.Title,
                invalid.Problem.Detail,
                invalid.Problem.Reason,
                invalid.Problem.Extensions);
        }

        var idempotencyKey = (header as IdempotencyKeyHeader.Result.Present)?.Key;

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

                // The deployment's restriction of the high priority is a capability of this
                // deployment for this consumer: it is decided with the other capabilities, before
                // the key is looked up, so a request the policy does not admit creates nothing.
                if (RequestFingerprintEncoder.DeclaredPriority(accepted.Request) == JobPriority.High
                    && !priorityPolicy.Value.AdmitsHigh(consumerId))
                {
                    return Refuse(
                        context,
                        metrics,
                        logger,
                        ErrorCodes.UnsupportedCapability,
                        StatusCodes.Status422UnprocessableEntity,
                        "Unsupported capability",
                        HighPriorityNotOfferedDetail,
                        RefusalReasons.Priority);
                }

                // The presentation is decided before any template or data work: a request whose
                // report could never be written in the values it names is refused at once, and
                // no job is created for it.
                var presentation = RequestedLocale.TryCreate(accepted.Request.Culture, accepted.Request.TimeZone);
                if (presentation is RequestedLocale.Result.Rejected rejectedPresentation)
                {
                    return Refuse(
                        context,
                        metrics,
                        logger,
                        ErrorCodes.RequestInvalid,
                        StatusCodes.Status400BadRequest,
                        RequestInvalidDetails.Title,
                        rejectedPresentation.Problem.Detail,
                        rejectedPresentation.Problem.Reason,
                        rejectedPresentation.Problem.Extensions);
                }

                if (presentation is not RequestedLocale.Result.Accepted acceptedPresentation)
                {
                    throw new InvalidOperationException("unhandled presentation result " + presentation.GetType().Name);
                }

                // The reference's syntax is decided before the database is touched, and a
                // value that cannot name a template this service could ever take is answered
                // with the one problem of a template that was not found.
                if (TemplateReferenceSyntax.Check(accepted.Request.TemplateId, accepted.Request.TemplateVersion)
                    is TemplateReferenceSyntax.ReferenceKind.NotFound)
                {
                    return Refuse(
                        context,
                        metrics,
                        logger,
                        ErrorCodes.TemplateNotFound,
                        StatusCodes.Status404NotFound,
                        TemplateReferenceSyntax.NotFoundTitle,
                        TemplateReferenceSyntax.NotFoundDetail,
                        TemplateReferenceSyntax.SyntaxReason);
                }

                return await AcceptedAsync(
                    context,
                    store,
                    versions,
                    metrics,
                    clock,
                    logger,
                    consumerId,
                    idempotencyKey,
                    acceptedPresentation.Locale,
                    accepted.Request).ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"unhandled read result {read.GetType().Name}");
        }
    }

    private static async Task<IResult> AcceptedAsync(
        HttpContext context,
        IJobStore store,
        ITemplateVersionStore versions,
        JobAcceptanceMetrics metrics,
        TimeProvider clock,
        ILogger logger,
        string consumerId,
        string? idempotencyKey,
        RequestedLocale presentation,
        CreateReportJobRequest request)
    {
        // A value that cannot be an identifier refers to an upload that cannot exist: it is
        // answered as a missing one, without a read.
        if (!UploadIdentifier.IsWellFormed(request.UploadId))
        {
            return NotFound(context, metrics, logger);
        }

        // The request's identity is computed from what it asked for and nothing else: no store,
        // no clock, no version resolved later. Every accepted job records it, with or without a key.
        var fingerprint = RequestFingerprintEncoder.Compute(consumerId, request);

        // A key already bound is answered here, before the state the request names is looked at,
        // so a retry always receives its first job even when its upload has become unusable or a
        // version it named has been taken by another consumer since. From here the client's
        // connection is not observed.
        if (idempotencyKey is not null)
        {
            IdempotencyLookup lookup;
            try
            {
                lookup = await store
                    .FindByIdempotencyKeyAsync(consumerId, idempotencyKey, fingerprint, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (JobStoreUnavailableException exception)
            {
                logger.LogWarning("The idempotency key of a job request could not be read ({ExceptionType})", exception.GetType().Name);
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

            if (lookup is IdempotencyLookup.SameRequest repeat)
            {
                return Replay(context, metrics, logger, repeat.ReportJobId);
            }

            if (lookup is IdempotencyLookup.DifferentRequest)
            {
                return Conflict(context, metrics, logger);
            }
        }

        // The versions already taken are read once, after every free check: a version this
        // consumer may not use is refused with the same problem as one that does not exist,
        // and a reference by name leaves with the release version it may fall back to. From
        // here the client's connection is not observed, so a request that is accepted is
        // accepted whatever the client does next.
        TakenTemplateVersionCheck.Answer availability;
        try
        {
            availability = await new TakenTemplateVersionCheck(versions)
                .CheckAsync(request.TemplateId, request.TemplateVersion, consumerId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TemplateStoreUnavailableException exception)
        {
            logger.LogWarning("The taken template versions could not be read ({ExceptionType})", exception.GetType().Name);
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

        if (!availability.Usable)
        {
            return Refuse(
                context,
                metrics,
                logger,
                ErrorCodes.TemplateNotFound,
                StatusCodes.Status404NotFound,
                TemplateReferenceSyntax.NotFoundTitle,
                TemplateReferenceSyntax.NotFoundDetail,
                TemplateReferenceSyntax.NotUsableReason);
        }

        var now = JobWireValues.TruncateToMillisecond(clock.GetUtcNow());
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            RequestedOutputs(request),
            consumerId,
            request.UploadId,
            request.TemplateId,
            request.TemplateVersion is null ? TemplateReferenceForm.ByName : TemplateReferenceForm.Exact,
            request.TemplateVersion,
            fingerprint)
        {
            IdempotencyKey = idempotencyKey,
            BaselineVersion = availability.BaselineVersion,
            Culture = presentation.Culture,
            TimeZone = presentation.TimeZone,
            DeclaredPriority = RequestFingerprintEncoder.DeclaredPriority(request),
            EffectivePriorityClass = RequestFingerprintEncoder.DeclaredPriority(request),
        };

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

        switch (result)
        {
            case JobAcceptanceResult.UploadUnusable:
                return NotFound(context, metrics, logger);

            case JobAcceptanceResult.SameRequest repeat:
                // The request raced another with the same key and the winner committed first.
                return Replay(context, metrics, logger, repeat.ReportJobId);

            case JobAcceptanceResult.DifferentRequest:
                return Conflict(context, metrics, logger);
        }

        var job = ((JobAcceptanceResult.Accepted)result).Job;
        var statusUrl = $"{RoutePrefix}/{job.ReportJobId}";
        Activity.Current?.SetTag(JobIdTag, job.ReportJobId);
        metrics.Accepted();
        // The reference's form is the log's, never a sent value: the syntax check has
        // already decided the request may name it.
        logger.LogInformation(
            "Job {ReportJobId} accepted for upload {UploadId} ({ReferenceForm}, {KeyUse})",
            job.ReportJobId,
            job.UploadId,
            TemplateReferenceSyntax.FormOf(request.TemplateVersion),
            idempotencyKey is null ? KeyUseWithout : KeyUseWith);
        return Results.Accepted(statusUrl, ReportJobAcceptedResponse.From(job, statusUrl));
    }

    // The answer to a repeat is the first answer, replayed: the consumer that lost it receives
    // exactly it, and the job's current state is the status read's. Nothing is written here.
    private static IResult Replay(HttpContext context, JobAcceptanceMetrics metrics, ILogger logger, string reportJobId)
    {
        var statusUrl = $"{RoutePrefix}/{reportJobId}";
        Activity.Current?.SetTag(JobIdTag, reportJobId);
        metrics.Repeated();
        logger.LogInformation("Job {ReportJobId} returned for a repeated request", reportJobId);
        return Results.Accepted(
            statusUrl,
            new ReportJobAcceptedResponse(reportJobId, JobWireValues.ToWire(ReportJobStatus.Queued), statusUrl));
    }

    // The one answer of a reused key: never the key, never the first job's identifier.
    private static IResult Conflict(HttpContext context, JobAcceptanceMetrics metrics, ILogger logger) =>
        Refuse(
            context,
            metrics,
            logger,
            ErrorCodes.IdempotencyConflict,
            StatusCodes.Status409Conflict,
            ConflictTitle,
            ConflictDetail,
            RefusalReasons.KeyReused);

    // The outputs an accepted request materializes: one per distinct requested format, in the order
    // the request first named it, each with its own freshly allocated identifier. Every token has
    // already been admitted (it is in the supported set), so the mapping is total; a format named
    // more than once is produced once (Decision D1, defence in depth — the request reader already
    // refuses a repeated output token before admission).
    private static IReadOnlyList<RequestedOutput> RequestedOutputs(CreateReportJobRequest request)
    {
        var outputs = new List<RequestedOutput>();
        var seen = new HashSet<OutputFormat>();
        foreach (var token in request.Outputs)
        {
            var format = ToFormat(token);
            if (seen.Add(format))
            {
                outputs.Add(new RequestedOutput(JobIdentifier.CreateOutput(), format));
            }
        }

        return outputs;
    }

    // The format an admitted output token names. Every token reaching here is in the supported set,
    // so the mapping is total and an unknown token is an invariant breach, not a request error.
    private static OutputFormat ToFormat(string output) => output switch
    {
        "html" => OutputFormat.Html,
        "pdf" => OutputFormat.Pdf,
        _ => throw new InvalidOperationException("an admitted output token has no format mapping"),
    };

    // Null when the request asks for what this increment offers; otherwise the one refusal and its log reason.
    private static (string Detail, string Reason)? UnsupportedDetail(CreateReportJobRequest request)
    {
        var unsupportedOutput = request.Outputs.Any(output => !SupportedOutputs.Contains(output, StringComparer.Ordinal));
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

        public const string KeyReused = "key-reused";

        public const string StoreUnavailable = "store-unavailable";

        public const string Priority = "priority";
    }

    // The key use a log line names, a constant of the two cases there are.
    private const string KeyUseWith = "with-key";

    private const string KeyUseWithout = "without-key";
}
