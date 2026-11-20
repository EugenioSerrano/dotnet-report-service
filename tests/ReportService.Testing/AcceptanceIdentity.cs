using System.Security.Cryptography;
using System.Text;
using ReportService.Jobs;

namespace ReportService.Testing;

/// <summary>
/// The identity a test gives a job acceptance when it is not exercising the product's own
/// encoder: a deterministic value over the upload the job reports on and, when it names one,
/// the version. Two acceptances of one upload and one version share it; an acceptance of
/// another upload does not. It exists so a fixture can build an acceptance without carrying
/// the request that would have produced it, and it is never the value the service computes.
/// </summary>
public static class AcceptanceIdentity
{
    /// <summary>The identity of an acceptance over <paramref name="uploadId"/>.</summary>
    /// <param name="uploadId">The upload the job reports on.</param>
    /// <param name="version">The version the acceptance names; null for a reference by name.</param>
    public static RequestFingerprint For(string uploadId, string? version = "1.0.0")
    {
        ArgumentNullException.ThrowIfNull(uploadId);
        return RequestFingerprint.FromDigest(
            SHA256.HashData(Encoding.UTF8.GetBytes("acceptance/v1\n" + uploadId + "\n" + (version ?? "(none)"))));
    }
}
