using Microsoft.Extensions.Logging;
using ReportService.Api.Http;
using ReportService.Api.Telemetry;
using ReportService.Api.Uploads;

namespace ReportService.Api.Consumers;

/// <summary>
/// The gate every upload operation passes first: it decides who the request may
/// act as, from the declared identity and the consumer registry, before the
/// request body is read and before any database or storage access. The registry
/// is the one the service configuration carries, and the caller hands over a
/// single snapshot of it, so a registry change that lands mid-request is seen by
/// the next request and not by the one already running. Nothing here touches the
/// database, the object store or the body. Every operation of this API starts
/// here, including the reads that take no body at all.
/// </summary>
public static class ConsumerAccess
{
    /// <summary>Decides whether <paramref name="request"/> may go on.</summary>
    /// <param name="request">The request being served.</param>
    /// <param name="registry">The registry snapshot this request captured.</param>
    /// <param name="requirement">Whether the consumer must be active as well as registered.</param>
    public static ConsumerAccessResult Resolve(
        HttpRequest request,
        ConsumerRegistrySnapshot registry,
        ConsumerRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registry);

        // No header at all is a missing declaration; a header that is present but unusable
        // (empty, repeated into several values, comma-joined or outside the grammar) is a
        // malformed one. Both are answered by the same refusal.
        if (!request.Headers.TryGetValue(ConsumerIdentity.HeaderName, out var declared) || declared.Count == 0)
        {
            return ConsumerAccessResult.Unknown(ConsumerIdentityState.Missing);
        }

        if (!ConsumerIdentity.TryRead(request, out var consumerId))
        {
            return ConsumerAccessResult.Unknown(ConsumerIdentityState.Malformed);
        }

        // The lookup is ordinal and exact: no trimming, no case folding, so a declaration
        // that differs from an entry by one character is simply not that entry.
        var entry = Find(registry, consumerId);
        if (entry is null)
        {
            return ConsumerAccessResult.Unknown(ConsumerIdentityState.Unregistered);
        }

        return requirement == ConsumerRequirement.Active && !entry.Active
            ? ConsumerAccessResult.RefuseInactive(consumerId)
            : ConsumerAccessResult.Allow(consumerId);
    }

    /// <summary>
    /// Answers a refused request: the one problem document of its condition, the log
    /// line that names the condition and the state, the span's error code and the
    /// counter. The declared value of an unknown identity is never logged, because it
    /// is unverified caller text; a registered consumer's identity may be.
    /// </summary>
    /// <param name="context">The request being served.</param>
    /// <param name="result">The gate's refusal.</param>
    /// <param name="operation">The operation, one of <see cref="ConsumerRefusalMetrics.Operations"/>.</param>
    /// <param name="logger">The log of the API's upload requests.</param>
    /// <param name="metrics">The process's instruments.</param>
    public static IResult Refuse(
        HttpContext context,
        ConsumerAccessResult result,
        string operation,
        ILogger logger,
        ConsumerRefusalMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);
        if (result.Allowed)
        {
            throw new ArgumentException("the gate allowed the request; there is nothing to refuse.", nameof(result));
        }

        var state = result.IdentityStateText!;
        System.Diagnostics.Activity.Current?.SetTag(UploadEndpoints.ErrorCodeTag, CodeOf(result));
        metrics.Refusal(CodeOf(result), operation, state);

        if (result.Inactive)
        {
            logger.LogInformation(
                "Request refused with {ErrorCode} for consumer {ConsumerId} ({Operation})",
                ErrorCodes.ConsumerInactive,
                result.ConsumerId,
                operation);
            return ApiProblem.Create(
                context,
                ErrorCodes.ConsumerInactive,
                StatusCodes.Status403Forbidden,
                InactiveTitle,
                InactiveDetail);
        }

        logger.LogInformation(
            "Request refused with {ErrorCode} ({Operation}, {IdentityState})",
            ErrorCodes.ConsumerUnknown,
            operation,
            state);
        return ApiProblem.Create(
            context,
            ErrorCodes.ConsumerUnknown,
            StatusCodes.Status403Forbidden,
            UnknownTitle,
            UploadEndpoints.ConsumerUnknownDetail);
    }

    /// <summary>What an answer says when the consumer is registered and inactive.</summary>
    public const string InactiveTitle = "Inactive consumer";

    /// <summary>What an answer says when the consumer is registered and inactive.</summary>
    public const string InactiveDetail = "The consumer is not active and cannot create uploads.";

    /// <summary>What an answer says when the request declares no usable identity.</summary>
    public const string UnknownTitle = "Unknown consumer";

    private static string CodeOf(ConsumerAccessResult result) =>
        result.Inactive ? ErrorCodes.ConsumerInactive : ErrorCodes.ConsumerUnknown;

    private static ConsumerEntry? Find(ConsumerRegistrySnapshot registry, string consumerId)
    {
        foreach (var entry in registry.Entries)
        {
            if (string.Equals(entry.ConsumerId, consumerId, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}
