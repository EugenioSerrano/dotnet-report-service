using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using ReportService.Jobs;

namespace ReportService.Api.Jobs;

/// <summary>
/// The identity of a job request: what the consumer asked for, written in one canonical
/// form and reduced to a SHA-256. Its inputs are the request and the consumer that sent
/// it — never a version resolved later, a baseline, a clock or a store — so a retry of
/// the same request has the same identity however the world changed between the two
/// calls, and two requests that differ in anything they asked for never share one.
/// </summary>
public static class RequestFingerprintEncoder
{
    /// <summary>The line the canonical form opens with; it names the form's version.</summary>
    public const string CanonicalForm = "report-job-request/v1";

    /// <summary>The member carrying the declared consumer.</summary>
    public const string ConsumerMember = "consumer";

    /// <summary>The member carrying the upload's identifier.</summary>
    public const string UploadIdMember = "data.uploadId";

    /// <summary>The member carrying the declared data profile.</summary>
    public const string ProfileMember = "data.profile";

    /// <summary>The member carrying the template's identifier.</summary>
    public const string TemplateIdMember = "template.id";

    /// <summary>The member carrying the version the request named, written only when it named one.</summary>
    public const string TemplateVersionMember = "template.version";

    /// <summary>The member carrying the culture the request named, written only when it named one.</summary>
    public const string CultureMember = "render.culture";

    /// <summary>The member carrying the time zone the request named, written only when it named one.</summary>
    public const string TimeZoneMember = "render.timeZone";

    /// <summary>The member carrying the requested outputs, one line per value in ordinal order.</summary>
    public const string OutputsMember = "outputs";

    /// <summary>
    /// The one ordered table the canonical form is written from, and the table the coverage
    /// guard reads: each entry names the member it writes and the request property it takes
    /// its value from, so a member can never be declared without being encoded or encoded
    /// without being declared. An entry whose accessor answers no value contributes nothing.
    /// </summary>
    public static IReadOnlyList<Member> Members { get; } =
    [
        new(ConsumerMember, "consumer", (_, consumerId) => [consumerId]),
        new(UploadIdMember, nameof(CreateReportJobRequest.UploadId), (request, _) => [request.UploadId]),
        new(ProfileMember, nameof(CreateReportJobRequest.Profile), (request, _) => [request.Profile]),
        new(TemplateIdMember, nameof(CreateReportJobRequest.TemplateId), (request, _) => [request.TemplateId]),
        new(TemplateVersionMember, nameof(CreateReportJobRequest.TemplateVersion), (request, _) =>
            request.TemplateVersion is { } version ? [version] : []),
        new(CultureMember, nameof(CreateReportJobRequest.Culture), (request, _) =>
            request.Culture is { } culture ? [culture] : []),
        new(TimeZoneMember, nameof(CreateReportJobRequest.TimeZone), (request, _) =>
            request.TimeZone is { } timeZone ? [timeZone] : []),
        new(OutputsMember, nameof(CreateReportJobRequest.Outputs), (request, _) =>
            [.. request.Outputs.OrderBy(output => output, StringComparer.Ordinal)]),
    ];

    /// <summary>
    /// Computes the identity of a request: the canonical form of <paramref name="request"/>
    /// for <paramref name="consumerId"/>, hashed with SHA-256 and stored as <c>v1:</c> and
    /// the digest's lower-case hexadecimal.
    /// </summary>
    /// <param name="consumerId">The declared consumer that sent the request.</param>
    /// <param name="request">The request as it was read.</param>
    public static RequestFingerprint Compute(string consumerId, CreateReportJobRequest request)
    {
        ArgumentException.ThrowIfNullOrEmpty(consumerId);
        ArgumentNullException.ThrowIfNull(request);

        var buffer = new ArrayBufferWriter<byte>();
        Write(buffer, CanonicalForm + "\n");
        foreach (var member in Members)
        {
            foreach (var value in member.Values(request, consumerId))
            {
                Write(buffer, member.Name);
                Write(buffer, "\t");
                Write(buffer, Encoding.UTF8.GetByteCount(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Write(buffer, "\t");
                Write(buffer, value);
                Write(buffer, "\n");
            }
        }

        return RequestFingerprint.FromDigest(SHA256.HashData(buffer.WrittenSpan));
    }

    // The canonical form's own encoding: no member's value can forge a boundary, whatever
    // characters it carries, because every value is preceded by its length in bytes.
    private static void Write(ArrayBufferWriter<byte> buffer, string text)
    {
        var sound = Encoding.UTF8.GetBytes(text);
        buffer.Write(sound);
    }

    /// <summary>
    /// One entry of the canonical form: the member's name, the property of the request it
    /// reads, and the accessor that answers the values it contributes.
    /// </summary>
    /// <param name="Name">The member's name in the canonical form.</param>
    /// <param name="RequestProperty">The request property the values come from.</param>
    /// <param name="Values">The values the member contributes, in the order they are written.</param>
    public sealed record Member(
        string Name,
        string RequestProperty,
        Func<CreateReportJobRequest, string, IReadOnlyList<string>> Values);
}
