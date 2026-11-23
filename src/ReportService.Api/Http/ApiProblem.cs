using System.Diagnostics;
using System.Text.Json;

namespace ReportService.Api.Http;

/// <summary>
/// The one way this API builds an error answer: an RFC 9457 problem document
/// carrying the stable <c>code</c> and the trace identifier of the request, with
/// the detail texts kept as constants in the code. No exception message, stack
/// trace, statement, connection detail or caller-supplied value is ever placed in
/// one — a caller learns the condition, never the internals of the service.
/// </summary>
public static class ApiProblem
{
    /// <summary>The content type of every error answer.</summary>
    public const string ContentType = "application/problem+json";

    /// <summary>The problem type of every answer: no further definition is published.</summary>
    public const string Type = "about:blank";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Builds an error answer for <paramref name="context"/>'s request.</summary>
    /// <param name="context">The request being served.</param>
    /// <param name="code">One of <see cref="ErrorCodes"/>.</param>
    /// <param name="status">The HTTP status of the answer.</param>
    /// <param name="title">The short, human-readable summary of the condition.</param>
    /// <param name="detail">The constant explanation of this occurrence, when there is one.</param>
    /// <param name="extensions">Further members of this condition (RFC 9457 §3.2), rendered after the code.</param>
    public static IResult Create(
        HttpContext context,
        string code,
        int status,
        string title,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var members = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = Type,
            ["title"] = title,
            ["status"] = status,
        };
        if (detail is not null)
        {
            members["detail"] = detail;
        }

        members["code"] = code;
        members["traceId"] = TraceId(context);

        foreach (var extension in extensions ?? new Dictionary<string, object?>())
        {
            members[extension.Key] = extension.Value;
        }

        return new ProblemResult(status, members);
    }

    // The identifier an operator searches the telemetry with; it names the request, never the caller.
    private static string TraceId(HttpContext context) =>
        Activity.Current?.Id ?? context.TraceIdentifier;

    private sealed class ProblemResult(int status, IReadOnlyDictionary<string, object?> members) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            var body = JsonSerializer.SerializeToUtf8Bytes(members, SerializerOptions);
            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = ContentType;
            httpContext.Response.ContentLength = body.Length;
            await httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
