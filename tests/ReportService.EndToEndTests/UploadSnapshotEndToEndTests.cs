using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using ReportService.Api.Http;
using ReportService.Api.Uploads;
using ReportService.Uploads;
using ReportService.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// Streaming, confirming and verifying a dataset on the running composition: the
/// pilot grouped dataset goes in plain and compressed and both end ready with the
/// same hash and size of the uncompressed content; a declared hash that does not
/// match ends rejected with no snapshot; and content sent to an upload that is
/// already ready is refused while its hash and size stay as they were.
/// </summary>
public sealed class UploadSnapshotEndToEndTests
{
    private const string Consumer = "dev-consumer";
    private const string PilotDataset = "header-items-v1.grouped.10k.json";
    private const string Pilot = "grouped";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task ThePilotDatasetStreamedPlainAndCompressedEndsReadyWithItsOwnHashAndSize()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var content = PilotContent();
        if (content is null)
        {
            Assert.Skip($"the pilot dataset is generated beforehand: run samples/datasets generate --dataset {Pilot}.");
        }

        var (bytes, expectedSha256) = content.Value;
        var appHost = await TestComposition.CreateAsync(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        }

        using var api = app.CreateHttpClient("api");

        // Plain.
        var plain = await StreamAndConfirmAsync(api, bytes, expectedSha256, gzip: false, cancellationToken);
        Assert.Equal("ready", await WaitForStatusAsync(api, plain, cancellationToken));
        var projection = await ReadAsync(api, plain, cancellationToken);
        Assert.Equal(expectedSha256, projection.GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, projection.GetProperty("sizeBytes").GetInt64());

        // Compressed: the same content, so the same hash and the same size.
        var compressed = await StreamAndConfirmAsync(api, bytes, expectedSha256, gzip: true, cancellationToken);
        Assert.Equal("ready", await WaitForStatusAsync(api, compressed, cancellationToken));
        var compressedProjection = await ReadAsync(api, compressed, cancellationToken);
        Assert.Equal(expectedSha256, compressedProjection.GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, compressedProjection.GetProperty("sizeBytes").GetInt64());

        // A declaration that does not match is refused and leaves no snapshot.
        var mismatched = await StreamAndConfirmAsync(api, bytes, new string('0', 64), gzip: false, cancellationToken);
        Assert.Equal("rejected", await WaitForStatusAsync(api, mismatched, cancellationToken));
        var rejected = await ReadAsync(api, mismatched, cancellationToken);
        Assert.Equal("DATA_INVALID", rejected.GetProperty("rejectionCode").GetString());
        Assert.Equal("sha256-mismatch", rejected.GetProperty("rejectionReason").GetString());
        Assert.Equal(JsonValueKind.Null, rejected.GetProperty("sha256").ValueKind);

        // Content sent to the ready upload is refused and nothing it says changes.
        using var again = await api.SendAsync(ContentRequest(plain, bytes, gzip: false), cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ErrorCodes.UploadStateConflict, (await ProblemAsync(again, cancellationToken)).GetProperty("code").GetString());
        var after = await ReadAsync(api, plain, cancellationToken);
        Assert.Equal(projection.GetProperty("sha256").GetString(), after.GetProperty("sha256").GetString());
        Assert.Equal(projection.GetProperty("sizeBytes").GetInt64(), after.GetProperty("sizeBytes").GetInt64());
    }

    private static async Task<string> StreamAndConfirmAsync(
        HttpClient api,
        byte[] bytes,
        string declaredSha256,
        bool gzip,
        CancellationToken cancellationToken)
    {
        using var created = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent("""{"profile":"header-items-v1","mode":"API_STREAM"}""", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadAsStringAsync(cancellationToken);
        using var projection = JsonDocument.Parse(body);
        var uploadId = projection.RootElement.GetProperty("uploadId").GetString()!;

        using var sent = await api.SendAsync(ContentRequest(uploadId, bytes, gzip), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        using var confirmed = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative))
            {
                Content = new StringContent($$"""{"sha256":"{{declaredSha256}}"}""", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        return uploadId;
    }

    private static HttpRequestMessage ContentRequest(string uploadId, byte[] bytes, bool gzip)
    {
        var content = new ByteArrayContent(gzip ? Gzip(bytes) : bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CreateUploadRequestReader.JsonMediaType);
        if (gzip)
        {
            content.Headers.ContentEncoding.Add("gzip");
        }

        var message = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
        {
            Content = content,
        };
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return message;
    }

    private static async Task<string?> WaitForStatusAsync(HttpClient api, string uploadId, CancellationToken cancellationToken)
    {
        string? status = null;
        await Wait.UntilAsync(
            $"the upload {uploadId} to end verified",
            async token =>
            {
                var projection = await ReadAsync(api, uploadId, token);
                status = projection.GetProperty("status").GetString();
                return status is "ready" or "rejected";
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return status;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient api, string uploadId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return buffer.ToArray();
    }

    // The pilot dataset and the hash the repository publishes for it, or nothing when it has not been generated.
    private static (byte[] Bytes, string Sha256)? PilotContent()
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "datasets");
        var dataset = Path.Combine(root, PilotDataset);
        var sums = Path.Combine(root, "SHA256SUMS");
        if (!File.Exists(dataset) || !File.Exists(sums))
        {
            return null;
        }

        var published = File.ReadAllLines(sums)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts => parts.Length >= 2 && string.Equals(parts[^1].TrimStart('*'), PilotDataset, StringComparison.Ordinal));
        if (published is null)
        {
            return null;
        }

        var bytes = File.ReadAllBytes(dataset);
        var computed = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(published[0].ToLowerInvariant(), computed);
        return (bytes, computed);
    }
}
