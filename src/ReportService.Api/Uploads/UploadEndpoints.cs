using ReportService.Uploads;

using System.Diagnostics;
using Microsoft.Extensions.Options;
using ReportService.Api.Http;
using ReportService.Storage;

namespace ReportService.Api.Uploads;

/// <summary>
/// The upload operations of the public API: a consumer opens a session for its
/// data and reads that session's status. Both answer the declared consumer only;
/// both refuse before anything is written, so a request the service does not
/// serve leaves no session behind. The conventions fixed here — the declared
/// identity header, the problem document with its stable code, the bounded
/// reading of a control body — are the ones every later operation of this API
/// follows.
/// </summary>
public static class UploadEndpoints
{
    /// <summary>The route the upload operations are served under.</summary>
    public const string RoutePrefix = "/api/v1/uploads";

    /// <summary>The category of the API's own log records.</summary>
    public const string LogCategory = "ReportService.Api.Uploads";

    /// <summary>The tag an upload's identifier is carried on in a span.</summary>
    public const string UploadIdTag = "upload.id";

    /// <summary>The tag a refusal's code is carried on in a span.</summary>
    public const string ErrorCodeTag = "error.code";

    /// <summary>What an answer says when the request declares no usable identity.</summary>
    public const string ConsumerUnknownDetail = "The request does not carry a usable consumer identity.";

    /// <summary>What an answer says when the creation document is over the bound.</summary>
    public const string BodyTooLargeDetail = "The request body is larger than this operation accepts.";

    /// <summary>What an answer says when the declared mode is not offered.</summary>
    public const string UnsupportedModeDetail =
        "The requested upload mode is not supported. Supported modes: " + UploadWireValues.ApiStream + ".";

    /// <summary>What an answer says when the declared profile is not admitted.</summary>
    public const string UnsupportedProfileDetail =
        "The declared data profile is not supported. Supported profiles: " + UploadWireValues.HeaderItemsV1 + ".";

    /// <summary>What an answer says when the named session is not the caller's or does not exist.</summary>
    public const string UploadNotFoundDetail = "The upload was not found.";

    /// <summary>Adds the upload operations to the application.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var uploads = endpoints.MapGroup(RoutePrefix);
        uploads.MapPost("", CreateAsync);
        uploads.MapGet("/{uploadId}", ReadAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        IUploadStore store,
        TimeProvider timeProvider,
        IOptions<UploadOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        if (!ConsumerIdentity.TryRead(context.Request, out var consumerId))
        {
            return Refuse(context, logger, ErrorCodes.ConsumerUnknown, StatusCodes.Status403Forbidden, "Unknown consumer", ConsumerUnknownDetail, RefusalReasons.Identity);
        }

        var read = await CreateUploadRequestReader.ReadAsync(context.Request, cancellationToken).ConfigureAwait(false);
        switch (read)
        {
            case CreateUploadRequestReader.Result.TooLarge:
                return Refuse(context, logger, ErrorCodes.QuotaExceeded, StatusCodes.Status413PayloadTooLarge, "Request too large", BodyTooLargeDetail, RefusalReasons.BodySize);

            case CreateUploadRequestReader.Result.Rejected rejected:
                return Refuse(
                    context,
                    logger,
                    ErrorCodes.RequestInvalid,
                    StatusCodes.Status400BadRequest,
                    RequestInvalidDetails.Title,
                    rejected.Problem.Detail,
                    rejected.Problem.Reason,
                    rejected.Problem.Extensions);

            case CreateUploadRequestReader.Result.Accepted accepted:
                var unsupported = UnsupportedDetail(accepted.Request);
                if (unsupported is { } refusal)
                {
                    return Refuse(context, logger, ErrorCodes.UnsupportedCapability, StatusCodes.Status422UnprocessableEntity, "Unsupported capability", refusal.Detail, refusal.Reason);
                }

                return await CreatedAsync(context, store, timeProvider, options.Value, consumerId, logger, cancellationToken).ConfigureAwait(false);

            default:
                throw new InvalidOperationException($"unhandled read result {read.GetType().Name}");
        }
    }

    private static async Task<IResult> CreatedAsync(
        HttpContext context,
        IUploadStore store,
        TimeProvider timeProvider,
        UploadOptions options,
        string consumerId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var uploadId = UploadIdentifier.Create();
        var now = UploadWireValues.TruncateToMillisecond(timeProvider.GetUtcNow());
        var record = new UploadRecord(
            uploadId,
            consumerId,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            ObjectKey.FromSegments("staging", uploadId, "content"),
            now,
            now + options.SessionLifetime);

        await store.CreateAsync(record, cancellationToken).ConfigureAwait(false);

        Activity.Current?.SetTag(UploadIdTag, record.UploadId);
        logger.LogInformation(
            "Upload {UploadId} created for consumer {ConsumerId}, expires at {ExpiresAt}",
            record.UploadId,
            record.ConsumerId,
            record.ExpiresAt);
        return Results.Created($"{RoutePrefix}/{record.UploadId}", UploadStatusResponse.From(record));
    }

    private static async Task<IResult> ReadAsync(
        HttpContext context,
        IUploadStore store,
        string uploadId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        if (!ConsumerIdentity.TryRead(context.Request, out var consumerId))
        {
            return Refuse(context, logger, ErrorCodes.ConsumerUnknown, StatusCodes.Status403Forbidden, "Unknown consumer", ConsumerUnknownDetail, RefusalReasons.Identity);
        }

        // A value that cannot be an identifier refers to a session that cannot exist: it is
        // answered as a missing one, without a read, so the answer says nothing about which
        // identifiers exist or how they are formed.
        if (!UploadIdentifier.IsWellFormed(uploadId))
        {
            return NotFound(context, logger);
        }

        var record = await store.FindAsync(uploadId, consumerId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(context, logger);
        }

        Activity.Current?.SetTag(UploadIdTag, record.UploadId);
        return Results.Ok(UploadStatusResponse.From(record));
    }

    // Null when the request asks for what this increment offers; otherwise the one refusal and its log reason.
    private static (string Detail, string Reason)? UnsupportedDetail(CreateUploadRequest request)
    {
        var unsupportedMode = !string.Equals(request.Mode, UploadWireValues.ApiStream, StringComparison.Ordinal);
        var unsupportedProfile = !string.Equals(request.Profile, UploadWireValues.HeaderItemsV1, StringComparison.Ordinal);
        return (unsupportedMode, unsupportedProfile) switch
        {
            (true, true) => (UnsupportedModeDetail + " " + UnsupportedProfileDetail, RefusalReasons.ModeAndProfile),
            (true, false) => (UnsupportedModeDetail, RefusalReasons.Mode),
            (false, true) => (UnsupportedProfileDetail, RefusalReasons.Profile),
            _ => null,
        };
    }

    private static IResult NotFound(HttpContext context, ILogger logger) =>
        Refuse(context, logger, ErrorCodes.InputNotFound, StatusCodes.Status404NotFound, "Upload not found", UploadNotFoundDetail, RefusalReasons.NotFound);

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

    // The refusal reasons written to the log: constants, never a value from the request.
    private static class RefusalReasons
    {
        public const string Identity = "identity";

        public const string BodySize = "body-size";

        public const string Mode = "mode";

        public const string Profile = "profile";

        public const string ModeAndProfile = "mode,profile";

        public const string NotFound = "not-found";
    }
}
