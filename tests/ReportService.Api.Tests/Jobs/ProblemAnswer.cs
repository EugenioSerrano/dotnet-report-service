using System.Net;
using System.Text.RegularExpressions;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// One problem answer as a case must compare it: the status, the media type, every header
/// but the one the transport writes, and the body with the request's trace identifier
/// replaced by one fixed value. Two answers that must not differ pass every part of this
/// comparison, so no member, header or byte order is left through which the service could
/// reveal what it was not allowed to look at.
/// </summary>
internal static partial class ProblemAnswer
{
    /// <summary>The header every answer carries for the transport's own clock; it is not a part of the answer.</summary>
    private const string DateHeader = "Date";

    /// <summary>Reads <paramref name="response"/> into the comparable shape.</summary>
    /// <param name="response">The answer.</param>
    /// <returns>The answer's status, media type, headers and canonical body.</returns>
    public static async Task<Captured> CaptureAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (string.Equals(header.Key, DateHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            headers.Add(new KeyValuePair<string, string>(header.Key.ToLowerInvariant(), string.Join(",", header.Value)));
        }

        headers.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return new Captured(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            headers,
            Canonical(body));
    }

    /// <summary>Asserts that two answers are the same answer, apart from the trace identifier.</summary>
    /// <param name="expected">The answer the other must equal.</param>
    /// <param name="actual">The answer under test.</param>
    public static void AssertEqual(Captured expected, Captured actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.MediaType, actual.MediaType);
        Assert.Equal(expected.Headers, actual.Headers);
        Assert.Equal(expected.Body, actual.Body);
    }

    /// <summary>Replaces the request's trace identifier, which must differ per request and names nothing.</summary>
    /// <param name="body">The answer's body as it was sent.</param>
    /// <returns>The body with its trace identifier replaced by a fixed value.</returns>
    private static string Canonical(string body) => TraceId().Replace(body, "\"traceId\":\"<trace>\"");

    [GeneratedRegex("\"traceId\":\"[^\"]*\"", RegexOptions.CultureInvariant)]
    private static partial Regex TraceId();

    /// <summary>One answer as it must compare: the status, the media type, the ordered headers and the canonical body.</summary>
    /// <param name="Status">The HTTP status.</param>
    /// <param name="MediaType">The media type of the body.</param>
    /// <param name="Headers">Every response and content header but the transport's date, lower-cased and ordered.</param>
    /// <param name="Body">The body with its trace identifier replaced by a fixed value.</param>
    public sealed record Captured(
        HttpStatusCode Status,
        string? MediaType,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        string Body);
}
