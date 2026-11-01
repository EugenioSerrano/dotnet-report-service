using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using ReportService.Api.Http;
using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.Api.Uploads;

/// <summary>
/// The operation that puts the consumer's report data into its upload: one body
/// per upload, streamed to the upload's staging object, counted as it arrives and
/// never held. The order of the decisions is fixed — the declared identity, the
/// identifier's shape, the media type, the declared encoding, the session's state,
/// the declared length — and nothing is read from the body before the state has
/// admitted the transfer. A transfer that crosses the size limit is stopped where
/// it is; one that ends early is recorded as interrupted; either way the upload
/// ends in a state the consumer can read back, and the storage port never sees a
/// failure it has to interpret.
/// </summary>
public static class UploadContentEndpoint
{
    /// <summary>The tag a refusal's code is carried on in a span.</summary>
    public const string ErrorCodeTag = UploadEndpoints.ErrorCodeTag;

    /// <summary>The tag the bytes received are carried on in a span.</summary>
    public const string ReceivedBytesTag = "upload.received_bytes";

    /// <summary>What an answer says when the body is larger than the upload may hold.</summary>
    public const string QuotaDetail = "The upload is larger than this service accepts.";

    /// <summary>What an answer says when the upload does not take content in its state.</summary>
    public const string StateConflictDetail = "The upload does not accept content in its current state.";

    /// <summary>What an answer says when the body ended before its declared end.</summary>
    public const string BodyIncompleteDetail = "The request body ended before its declared end.";

    // The refusal reasons written to the log: constants, never a value from the request.
    private const string IdentityReason = "identity";
    private const string NotFoundReason = "not-found";
    private const string StateReason = "state";
    private const string QuotaReason = "quota";
    private const string StorageReason = "storage";

    /// <summary>Adds the content operation to the application.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapUploadContentEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGroup(UploadEndpoints.RoutePrefix).MapPut("/{uploadId}/content", SendAsync);
        return endpoints;
    }

    private static async Task<IResult> SendAsync(
        HttpContext context,
        IUploadStore store,
        IObjectStore objects,
        IOptions<UploadOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(UploadEndpoints.LogCategory);

        if (!ConsumerIdentity.TryRead(context.Request, out var consumerId))
        {
            return Refuse(context, logger, ErrorCodes.ConsumerUnknown, StatusCodes.Status403Forbidden, "Unknown consumer", UploadEndpoints.ConsumerUnknownDetail, IdentityReason);
        }

        // A value that cannot be an identifier refers to an upload that cannot exist.
        var uploadId = context.Request.RouteValues["uploadId"] as string ?? string.Empty;
        if (!UploadIdentifier.IsWellFormed(uploadId))
        {
            return NotFound(context, logger);
        }

        if (!IsJson(context.Request.ContentType))
        {
            return RequestInvalid(context, logger, InvalidRequestBody.MediaType());
        }

        var encoding = ContentEncodingHeader.Classify(context.Request);

        var record = await store.FindAsync(uploadId, consumerId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(context, logger);
        }

        if (record.Status != UploadStatus.Created)
        {
            return StateConflict(context, logger, StateConflictDetail);
        }

        if (!await store.TryStartTransferAsync(uploadId, consumerId, encoding, cancellationToken).ConfigureAwait(false))
        {
            // A concurrent request moved the session first; the answer is the one its
            // state now admits.
            var latest = await store.FindAsync(uploadId, consumerId, cancellationToken).ConfigureAwait(false);
            return latest is null ? NotFound(context, logger) : StateConflict(context, logger, StateConflictDetail);
        }

        var limit = options.Value.MaxStoredBytes;        if (context.Request.ContentLength is { } declaredLength && declaredLength > limit)
        {
            // The declared length alone refuses the transfer, before a byte is read.
            await store.RejectTransferAsync(uploadId, UploadRejectionCode.QuotaExceeded, UploadRejectionReason.StoredBytes, 0, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Upload {UploadId} content stopped at the size limit after {ReceivedBytes} bytes", uploadId, 0L);
            return TooLarge(context, logger);
        }

        // The server's own body limit would refuse a large upload with its own answer;
        // the service's own limit, applied to the bytes as they arrive, replaces it.
        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
        {
            bodySize.MaxRequestBodySize = null;
        }

        using var body = new UploadBodyStream(context.Request.Body, limit, context.Request.ContentLength);
        StorageResult<ObjectWrite>? written = null;
        try
        {
            written = await objects.WriteAsync(record.StagingKey, body, cancellationToken).ConfigureAwait(false);
        }
        catch (UploadQuotaExceededException)
        {
            // Recorded by the reader; handled below from its record.
        }
        catch (UploadTransferInterruptedException)
        {
            // Recorded by the reader; handled below from its record.
        }
        catch (StorageException)
        {
            // A storage failure of any outcome is answered as the service's own condition,
            // and the bytes that did arrive are recorded as an interruption.
        }

        var landed = written is { Succeeded: true };

        // The reader's own record decides first: it is the only account of how the body
        // itself ended, whatever the port reported about the write.
        switch (body.RecordedFailure)
        {
            case UploadStreamFailure.QuotaExceeded:
                if (landed)
                {
                    await DeleteAsync(objects, record.StagingKey, logger, cancellationToken).ConfigureAwait(false);
                }

                await store.RejectTransferAsync(
                    uploadId,
                    UploadRejectionCode.QuotaExceeded,
                    UploadRejectionReason.StoredBytes,
                    body.BytesRead,
                    cancellationToken).ConfigureAwait(false);
                SetTags(uploadId, body.BytesRead);
                logger.LogInformation("Upload {UploadId} content stopped at the size limit after {ReceivedBytes} bytes", uploadId, body.BytesRead);
                return TooLarge(context, logger);

            case UploadStreamFailure.Interrupted:
                if (landed)
                {
                    await DeleteAsync(objects, record.StagingKey, logger, cancellationToken).ConfigureAwait(false);
                }

                await store.RecordTransferAsync(uploadId, UploadContentOutcome.Interrupted, body.BytesRead, null, null, cancellationToken).ConfigureAwait(false);
                SetTags(uploadId, body.BytesRead);
                logger.LogInformation("Upload {UploadId} content ended early after {ReceivedBytes} bytes", uploadId, body.BytesRead);
                return RequestInvalid(context, logger, InvalidRequestBody.BodyIncomplete());

            default:
                break;
        }

        if (!landed)
        {
            // The bytes did not land as they were received: the transfer is recorded as an
            // interruption and the consumer is told to try again with a new upload.
            await store.RecordTransferAsync(uploadId, UploadContentOutcome.Interrupted, body.BytesRead, null, null, cancellationToken).ConfigureAwait(false);
            SetTags(uploadId, body.BytesRead);
            logger.LogWarning(
                "Upload {UploadId} content could not be stored ({StorageOutcome})",
                uploadId,
                (written?.Outcome ?? StorageOutcome.Transient).ToString());
            return Refuse(context, logger, ErrorCodes.ServiceUnavailable, StatusCodes.Status503ServiceUnavailable, "Service unavailable", ServiceUnavailableDetail, StorageReason);
        }

        var stored = written!.Value!;
        if (stored.Size != body.BytesRead)
        {
            await store.RecordTransferAsync(uploadId, UploadContentOutcome.Interrupted, body.BytesRead, null, null, cancellationToken).ConfigureAwait(false);
            SetTags(uploadId, body.BytesRead);
            logger.LogWarning(
                "Upload {UploadId} content could not be stored ({StorageOutcome})",
                uploadId,
                StorageOutcome.Success.ToString());
            return Refuse(context, logger, ErrorCodes.ServiceUnavailable, StatusCodes.Status503ServiceUnavailable, "Service unavailable", ServiceUnavailableDetail, StorageReason);
        }

        await store.RecordTransferAsync(uploadId, UploadContentOutcome.Complete, body.BytesRead, stored.Size, stored.Sha256, cancellationToken).ConfigureAwait(false);

        var current = await store.FindAsync(uploadId, consumerId, cancellationToken).ConfigureAwait(false) ?? record;
        SetTags(uploadId, body.BytesRead);
        logger.LogInformation(
            "Upload {UploadId} content received: {ReceivedBytes} bytes, encoding {Encoding}",
            uploadId,
            body.BytesRead,
            UploadWireValues.ToStoredText(encoding));
        return Results.Ok(UploadStatusResponse.From(current));
    }

    private const string ServiceUnavailableDetail = "The service could not complete the request for a cause on its side.";

    private static void SetTags(string uploadId, long receivedBytes)
    {
        Activity.Current?.SetTag(UploadEndpoints.UploadIdTag, uploadId);
        Activity.Current?.SetTag(ReceivedBytesTag, receivedBytes);
    }

    private static bool IsJson(string? contentType) =>
        Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
        && string.Equals(mediaType.MediaType.Value, CreateUploadRequestReader.JsonMediaType, StringComparison.OrdinalIgnoreCase);

    private static async Task DeleteAsync(IObjectStore objects, ObjectKey key, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await objects.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            if (!deleted.Succeeded)
            {
                logger.LogWarning("A stored object could not be deleted ({StorageOutcome})", deleted.Outcome.ToString());
            }
        }
        catch (StorageException exception)
        {
            logger.LogWarning("A stored object could not be deleted ({StorageOutcome})", exception.Outcome.ToString());
        }
    }

    private static IResult NotFound(HttpContext context, ILogger logger) =>
        Refuse(context, logger, ErrorCodes.InputNotFound, StatusCodes.Status404NotFound, "Upload not found", UploadEndpoints.UploadNotFoundDetail, NotFoundReason);

    private static IResult StateConflict(HttpContext context, ILogger logger, string detail) =>
        Refuse(context, logger, ErrorCodes.UploadStateConflict, StatusCodes.Status409Conflict, "Upload state conflict", detail, StateReason);

    private static IResult RequestInvalid(HttpContext context, ILogger logger, InvalidRequestBody problem) =>
        Refuse(context, logger, ErrorCodes.RequestInvalid, StatusCodes.Status400BadRequest, RequestInvalidDetails.Title, problem.Detail, problem.Reason, problem.Extensions);

    private static IResult TooLarge(HttpContext context, ILogger logger)
    {
        // The transfer is stopped here: the connection closes with the answer, so no more
        // of the body is read for a request that can never succeed.
        context.Response.Headers.Connection = "close";
        return Refuse(context, logger, ErrorCodes.QuotaExceeded, StatusCodes.Status413PayloadTooLarge, "Upload too large", QuotaDetail, QuotaReason);
    }

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
        Activity.Current?.SetTag(ErrorCodeTag, code);
        logger.LogInformation("Upload request refused with {ErrorCode} ({Reason})", code, reason);
        return ApiProblem.Create(context, code, status, title, detail, extensions);
    }
}
