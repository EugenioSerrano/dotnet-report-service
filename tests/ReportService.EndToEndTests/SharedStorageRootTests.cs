using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// The three processes of the composition share one storage root: an object
/// written by the PDF worker — inside its container, as its non-root user —
/// is read and verified by the API and by the orchestration worker, and a
/// corrupted copy is detected as an integrity failure by both. The run's
/// evidence (paths, the worker's user, the object operations) is attached to
/// the test result.
/// </summary>
public sealed class SharedStorageRootTests
{
    private const string ContainerRoot = "/data/storage";
    private const int ObjectSize = 1024 * 1024;

    // The first run may build the PDF base image and publish the render worker image.
    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly string[] Resources = ["temporal", "render-worker", "orchestration-worker", "api"];

    [Fact]
    public async Task ObjectWrittenByThePdfWorkerIsVerifiedByTheOtherProcessesAndCorruptionIsDetected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evidence = new StringBuilder();
        var hostRoot = Path.Combine(RepositoryPaths.Root, ".storage");
        var runId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var first = ObjectKey.FromSegments("staging", "probe", "e2e", runId);
        var second = ObjectKey.FromSegments("staging", "probe", "e2e", runId + "-b");
        evidence.AppendLine(CultureInfo.InvariantCulture, $"host root: {hostRoot}; container root: {ContainerRoot}; keys: {first}, {second}");

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ReportService_AppHost>(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            foreach (var resource in Resources)
            {
                await app.ResourceNotifications.WaitForResourceHealthyAsync(resource, deadline.Token);
            }
        }

        using var worker = app.CreateHttpClient("render-worker", "http");
        using var api = app.CreateHttpClient("api");
        using var orchestration = app.CreateHttpClient("orchestration-worker");
        try
        {
            // 1. The PDF worker writes the object.
            var content = Content(seed: 1);
            var expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
            var written = await WriteAsync(worker, first, content, cancellationToken);
            Assert.Equal(ObjectSize, written.Size);
            Assert.Equal(expectedSha256, written.Sha256);
            evidence.AppendLine(CultureInfo.InvariantCulture, $"write by the PDF worker: {first} size {written.Size} sha256 {written.Sha256}");

            var owner = await ContainerOwnerAsync(first, cancellationToken);
            evidence.AppendLine(CultureInfo.InvariantCulture, $"owner of the object inside the PDF worker's container (uid:gid): {owner}");
            Assert.StartsWith("1654:", owner, StringComparison.Ordinal);

            // 2. The API and 3. the orchestration worker read it, verified.
            Assert.True(await ReadVerifiedAsync(api, first, written, HttpStatusCode.OK, cancellationToken));
            Assert.True(await ReadVerifiedAsync(orchestration, first, written, HttpStatusCode.OK, cancellationToken));
            evidence.AppendLine("verified read by the API: 200; verified read by the orchestration worker: 200");

            // 4. A corrupted copy is an integrity failure for both.
            var hostPath = Path.Combine([hostRoot, .. first.Segments]);
            var bytes = await File.ReadAllBytesAsync(hostPath, cancellationToken);
            bytes[ObjectSize / 2] ^= 0xff;
            await File.WriteAllBytesAsync(hostPath, bytes, cancellationToken);
            Assert.False(await ReadVerifiedAsync(api, first, written, HttpStatusCode.UnprocessableEntity, cancellationToken));
            Assert.False(await ReadVerifiedAsync(orchestration, first, written, HttpStatusCode.UnprocessableEntity, cancellationToken));
            evidence.AppendLine(CultureInfo.InvariantCulture, $"one byte flipped on the host at {hostPath}; API: 422; orchestration worker: 422");

            // 5. A wrong expected hash on an untouched object is an integrity failure too.
            var other = await WriteAsync(worker, second, Content(seed: 2), cancellationToken);
            var wrong = other with { Sha256 = expectedSha256 };
            Assert.False(await ReadVerifiedAsync(api, second, wrong, HttpStatusCode.UnprocessableEntity, cancellationToken));
            evidence.AppendLine("wrong expected hash on an untouched object, API: 422");

            // 6. Every process reports its storage check healthy.
            AssertStorageHealthy(await HealthAsync(api, cancellationToken));
            AssertStorageHealthy(await HealthAsync(orchestration, cancellationToken));
            AssertStorageHealthy(await HealthAsync(worker, cancellationToken));
            evidence.AppendLine("storage checks: API Healthy; orchestration worker Healthy (writable, readable); PDF worker Healthy (writable, readable)");
        }
        finally
        {
            // Remove this run's objects through the port; deleting is idempotent.
            var store = FilesystemObjectStore.Open(
                new FilesystemStorageOptions { Root = hostRoot },
                hostRoot,
                TimeProvider.System,
                NullLogger<FilesystemObjectStore>.Instance);
            await store.DeleteAsync(first, CancellationToken.None);
            await store.DeleteAsync(second, CancellationToken.None);
            TestContext.Current.TestOutputHelper?.WriteLine(evidence.ToString());
            TestContext.Current.AddAttachment("shared-storage-root", evidence.ToString());
        }
    }

    private static byte[] Content(int seed)
    {
        var content = new byte[ObjectSize];
        new Random(seed).NextBytes(content);
        return content;
    }

    private static async Task<(long Size, string Sha256)> WriteAsync(HttpClient client, ObjectKey key, byte[] content, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(new Uri($"/diagnostics/storage/{key}", UriKind.Relative), new ByteArrayContent(content), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(key.ToString(), body.GetProperty("key").GetString());
        return (body.GetProperty("size").GetInt64(), body.GetProperty("sha256").GetString()!);
    }

    // Reads the object through a process's diagnostics; returns whether it was verified.
    private static async Task<bool> ReadVerifiedAsync(HttpClient client, ObjectKey key, (long Size, string Sha256) expected, HttpStatusCode status, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            new Uri(string.Create(CultureInfo.InvariantCulture, $"/diagnostics/storage/{key}?size={expected.Size}&sha256={expected.Sha256}"), UriKind.Relative),
            cancellationToken);
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (status != HttpStatusCode.OK)
        {
            Assert.Equal("IntegrityFailure", body.GetProperty("outcome").GetString());
            return false;
        }

        return body.GetProperty("verified").GetBoolean();
    }

    private static async Task<JsonDocument> HealthAsync(HttpClient client, CancellationToken cancellationToken) =>
        JsonDocument.Parse(await client.GetStringAsync(new Uri("/health", UriKind.Relative), cancellationToken));

    private static void AssertStorageHealthy(JsonDocument health)
    {
        using (health)
        {
            var check = Assert.Single(health.RootElement.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "storage");
            Assert.Equal("Healthy", check.GetProperty("status").GetString());
            Assert.True(check.GetProperty("data").GetProperty("writable").GetBoolean());
            Assert.True(check.GetProperty("data").GetProperty("readable").GetBoolean());
        }
    }

    // The owner of the object as the PDF worker's container sees it (only inspects; removes nothing).
    private static async Task<string> ContainerOwnerAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var names = await DockerAsync(["ps", "--filter", "name=render-worker-", "--format", "{{.Names}}"], cancellationToken);
        var container = Assert.Single(names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return (await DockerAsync(["exec", container, "stat", "-c", "%u:%g", $"{ContainerRoot}/{key}"], cancellationToken)).Trim();
    }

    private static async Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, $"docker {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}
