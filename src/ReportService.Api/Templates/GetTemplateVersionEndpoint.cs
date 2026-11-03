using System.Diagnostics;
using System.Text.RegularExpressions;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Telemetry;
using ReportService.Api.Uploads;
using ReportService.TemplatePackages.Records;
using ReportService.TemplatePackages.Versioning;

namespace ReportService.Api.Templates;

/// <summary>
/// The owner's read of a taken template version: the operation that answers what the
/// service fixed when it took a version, so a consumer can pin what its jobs render
/// without ever reaching the template repository. The operation decides in a fixed
/// order — who the request may act as, then whether the name can be a taken version
/// at all, then whose version it is — and it reads the metadata record only: it
/// calls no storage and no template source, and a consumer learns nothing about the
/// versions that are not its own.
/// </summary>
public static partial class GetTemplateVersionEndpoint
{
    /// <summary>The route the template operations are served under.</summary>
    public const string RoutePrefix = "/api/v1/templates";

    /// <summary>The category of the API's template log records.</summary>
    public const string LogCategory = "ReportService.Api.Templates";

    /// <summary>What an answer says when the version was never taken or belongs to another consumer.</summary>
    public const string NotFoundTitle = "Template not found";

    /// <summary>What an answer says when the version was never taken or belongs to another consumer.</summary>
    public const string NotFoundDetail = "The template version was not found.";

    /// <summary>What an answer says when the records could not be read.</summary>
    public const string ServiceUnavailableTitle = "Service unavailable";

    /// <summary>What an answer says when the records could not be read.</summary>
    public const string ServiceUnavailableDetail =
        "The template version could not be read; the service cannot complete the request now.";

    /// <summary>The tag a template's identifier is carried on in a span.</summary>
    public const string TemplateIdTag = "template.id";

    /// <summary>Adds the template operations to the application.</summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapGetTemplateVersionEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet($"{RoutePrefix}/{{templateId}}/versions/{{version}}", ReadAsync);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(
        HttpContext context,
        ITemplateVersionStore store,
        ConsumerRegistry registry,
        ConsumerRefusalMetrics refusals,
        string templateId,
        string version,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);

        // Reading is kept for a consumer that was made inactive: the gate asks only that
        // it is registered, as reading an upload's status does.
        var gate = ConsumerAccess.Resolve(context.Request, registry.Current, ConsumerRequirement.Registered);
        if (!gate.Allowed)
        {
            return ConsumerAccess.Refuse(
                context, gate, ConsumerRefusalMetrics.TemplateVersionOperation, logger, refusals);
        }

        var consumerId = gate.ConsumerId!;

        // A name that cannot be a taken version's refers to a version that cannot exist: it
        // is answered as a missing one, without a read, so the answer says nothing about
        // which versions exist or how their names are formed.
        if (!IsTemplateIdentifier(templateId) || !SemanticVersionSyntax.IsValid(version))
        {
            return NotFound(context, logger);
        }

        TemplateVersionRecord? record;
        try
        {
            record = await store.FindAsync(templateId, version, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplateStoreUnavailableException exception)
        {
            // The cause stays inside the service: the type is what an operator searches for,
            // and the message of a driver can carry a connection detail.
            logger.LogWarning("The template version store did not answer ({ExceptionType})", exception.GetType().Name);
            return ServiceUnavailable(context, logger);
        }

        // Another consumer's version is answered exactly like one that was never taken: the
        // owner is the record's, never the caller's, and sharing is not offered yet.
        if (record is null || !string.Equals(record.OwnerConsumerId, consumerId, StringComparison.Ordinal))
        {
            return NotFound(context, logger);
        }

        Activity.Current?.SetTag(TemplateIdTag, record.TemplateId);
        return Results.Ok(TemplateVersionResponse.From(record));
    }

    /// <summary>A refusal of a name that cannot be, or is not, a taken version.</summary>
    private static IResult NotFound(HttpContext context, ILogger logger) =>
        Refuse(
            context,
            logger,
            ErrorCodes.TemplateNotFound,
            StatusCodes.Status404NotFound,
            NotFoundTitle,
            NotFoundDetail,
            RefusalReasons.NotFound);

    private static IResult ServiceUnavailable(HttpContext context, ILogger logger) =>
        Refuse(
            context,
            logger,
            ErrorCodes.ServiceUnavailable,
            StatusCodes.Status503ServiceUnavailable,
            ServiceUnavailableTitle,
            ServiceUnavailableDetail,
            RefusalReasons.StoreUnavailable);

    private static IResult Refuse(
        HttpContext context,
        ILogger logger,
        string code,
        int status,
        string title,
        string detail,
        string reason)
    {
        Activity.Current?.SetTag(UploadEndpoints.ErrorCodeTag, code);
        logger.LogInformation("Template request refused with {ErrorCode} ({Reason})", code, reason);
        return ApiProblem.Create(context, code, status, title, detail);
    }

    // A template identifier is the grammar of the version's folder and of the record's key:
    // lower-case letters, digits and the three separators, never at an edge, at most 128
    // characters. A value outside it can never name a version that was taken.
    private static bool IsTemplateIdentifier(string? templateId) =>
        templateId is not null && TemplateIdentifier().IsMatch(templateId);

    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9._-]{0,126}[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateIdentifier();

    // The refusal reasons written to the log: constants, never a value from the request.
    private static class RefusalReasons
    {
        public const string NotFound = "not-found";

        public const string StoreUnavailable = "store-unavailable";
    }
}
