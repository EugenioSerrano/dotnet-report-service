using System.Diagnostics;
using Microsoft.Extensions.Options;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Telemetry;
using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.Api.Uploads;

/// <summary>
/// The operation that confirms an upload: it records the request that starts the
/// background verification and answers before any of that work runs. The declared
/// content hash is optional and is compared only by the verification; this
/// operation decides nothing about the content, so it writes no byte to storage and
/// holds no engine client. The confirmation is also what puts an end to the
/// upload's life: it records, beside the request, the instant until which the
/// verification may still fix a snapshot. A confirmation may be repeated �?" the
/// answer is then the upload's current state, however that state came about �?" but
/// only one confirmation can move an upload, and a repeated one may not declare a
/// different hash.
/// </summary>
public static class CompleteUploadEndpoint
{
    /// <summary>What an answer says when the upload has nothing to confirm.</summary>
    public const string NoContentDetail = "The upload has no complete content to confirm.";

    /// <summary>What an answer says when a repeated confirmation declares another hash.</summary>
    public const string DifferentDeclarationDetail = "A repeated confirmation cannot declare a different content hash.";

    private const string NotFoundReason = "not-found";
    private const string StateReason = "state";
    private const string BodySizeReason = "body-size";

    /// <summary>Adds the confirmation operation to the application.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapCompleteUploadEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGroup(UploadEndpoints.RoutePrefix).MapPost("/{uploadId}/complete", ConfirmAsync);
        return endpoints;
    }

    private static async Task<IResult> ConfirmAsync(
        HttpContext context,
        IUploadStore store,
        IObjectStore objects,
        ConsumerRegistry registry,
        ConsumerRefusalMetrics refusals,
        IOptions<UploadOptions> options,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(UploadEndpoints.LogCategory);

        // Confirming is kept for a consumer that was made inactive: verification itself
        // never consults the registry.
        var gate = ConsumerAccess.Resolve(context.Request, registry.Current, ConsumerRequirement.Registered);
        if (!gate.Allowed)
        {
            return ConsumerAccess.Refuse(context, gate, ConsumerRefusalMetrics.ConfirmOperation, logger, refusals);
        }

        var consumerId = gate.ConsumerId!;

        var uploadId = context.Request.RouteValues["uploadId"] as string ?? string.Empty;
        if (!UploadIdentifier.IsWellFormed(uploadId))
        {
            return NotFound(context, logger);
        }

        var read = await CompleteUploadRequestReader.ReadAsync(context.Request, cancellationToken).ConfigureAwait(false);
        string? declared;
        switch (read)
        {
            case CompleteUploadRequestReader.Result.TooLarge:
                return Refuse(context, logger, ErrorCodes.QuotaExceeded, StatusCodes.Status413PayloadTooLarge, "Request too large", UploadEndpoints.BodyTooLargeDetail, BodySizeReason);

            case CompleteUploadRequestReader.Result.Rejected rejected:
                return Refuse(context, logger, ErrorCodes.RequestInvalid, StatusCodes.Status400BadRequest, RequestInvalidDetails.Title, rejected.Problem.Detail, rejected.Problem.Reason, rejected.Problem.Extensions);

            case CompleteUploadRequestReader.Result.Accepted accepted:
                declared = accepted.Sha256;
                break;

            default:
                throw new InvalidOperationException($"unhandled read result {read.GetType().Name}");
        }

        var record = await store.FindAsync(uploadId, consumerId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(context, logger);
        }

        var now = UploadWireValues.TruncateToMillisecond(clock.GetUtcNow());
        record = await UploadExpiryStep.ApplyAsync(store, objects, record, now, logger, cancellationToken)
            .ConfigureAwait(false);

        if (record.Status == UploadStatus.Uploading && record.ContentOutcome is not null)
        {
            var deadline = now + options.Value.VerificationDeadline;
            if (!await store.ConfirmAsync(uploadId, consumerId, declared, now, now, deadline, cancellationToken).ConfigureAwait(false))
            {
                // A concurrent change moved the session, or its session ended first: the
                // answer is the one its state now admits.
                var current = await store.FindAsync(uploadId, consumerId, cancellationToken).ConfigureAwait(false);
                return current is null
                    ? NotFound(context, logger)
                    : AnswerByState(context, logger, current, declared);
            }

            var confirmed = await store.FindAsync(uploadId, consumerId, cancellationToken).ConfigureAwait(false) ?? record;
            Activity.Current?.SetTag(UploadEndpoints.UploadIdTag, uploadId);
            logger.LogInformation("Upload {UploadId} confirmed; verification requested", uploadId);
            return Accepted(context, confirmed);
        }

        return AnswerByState(context, logger, record, declared);
    }

    // The answer an upload's own state admits, for a confirmation that moved nothing.
    private static IResult AnswerByState(HttpContext context, ILogger logger, UploadRecord record, string? declared)
    {
        switch (record.Status)
        {
            case UploadStatus.Created:
            case UploadStatus.Uploading:
                return Refuse(context, logger, ErrorCodes.UploadStateConflict, StatusCodes.Status409Conflict, UploadEndpoints.StateConflictTitle, NoContentDetail, StateReason);

            case UploadStatus.Expired when record.CompletedAt is null:
                // The upload ran out of session before it was ever confirmed: there is
                // nothing to repeat, and nothing is recorded.
                return Refuse(context, logger, ErrorCodes.UploadStateConflict, StatusCodes.Status409Conflict, UploadEndpoints.StateConflictTitle, UploadEndpoints.ExpiredDetail, StateReason);

            default:
                // A repeated confirmation: it repeats the current state, unless it declares
                // another hash than the one this upload was confirmed with.
                if (declared is not null && !string.Equals(declared, record.DeclaredSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return Refuse(context, logger, ErrorCodes.UploadStateConflict, StatusCodes.Status409Conflict, UploadEndpoints.StateConflictTitle, DifferentDeclarationDetail, StateReason);
                }

                Activity.Current?.SetTag(UploadEndpoints.UploadIdTag, record.UploadId);
                return record.Status == UploadStatus.Verifying
                    ? Accepted(context, record)
                    : Results.Ok(UploadStatusResponse.From(record));
        }
    }

    private static IResult Accepted(HttpContext context, UploadRecord record) =>
        Results.Accepted($"{UploadEndpoints.RoutePrefix}/{record.UploadId}", UploadStatusResponse.From(record));

    private static IResult NotFound(HttpContext context, ILogger logger) =>
        Refuse(context, logger, ErrorCodes.InputNotFound, StatusCodes.Status404NotFound, UploadEndpoints.NotFoundTitle, UploadEndpoints.UploadNotFoundDetail, NotFoundReason);

    private static IResult Refuse(
        HttpContext context,
        ILogger logger,
        string code,
        int status,
        string title,
        string detail,
        string reason,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        Activity.Current?.SetTag(UploadEndpoints.ErrorCodeTag, code);
        logger.LogInformation("Upload request refused with {ErrorCode} ({Reason})", code, reason);
        return ApiProblem.Create(context, code, status, title, detail, extensions);
    }
}
