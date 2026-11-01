using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReportService.ServiceDefaults;
using ReportService.Storage;

namespace ReportService.Api.Tests.Storage;

/// <summary>
/// The API hosted in memory with a storage root of the test's own: provider
/// selection and root checks at start, the storage health check, and the
/// development-only diagnostics.
/// </summary>
public sealed class StorageHostingTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private string Root => Path.Combine(_directory.Path, "storage");

    [Fact]
    public async Task UnknownProviderStopsTheStartNamingTheSetting()
    {
        await using var api = Api(("Storage:Provider", "cloud"));

        var error = FindConfigurationError(Record.Exception(() => api.CreateClient()));

        Assert.Equal("setting 'Storage:Provider': is not a known provider (known: filesystem)", error.Message);
    }

    [Fact]
    public async Task ProcessOutsideDevelopmentHasNoDefaultProvider()
    {
        await using var api = Api().WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var error = FindConfigurationError(Record.Exception(() => api.CreateClient()));

        Assert.Equal("Storage:Provider", error.Setting);
        Assert.Equal("is required; no provider is selected by default", error.Reason);
    }

    [Fact]
    public async Task MissingRootThatMayNotBeCreatedStopsTheStartWithoutItsPath()
    {
        await using var api = Api(("Storage:Filesystem:CreateIfMissing", "false"));

        var error = FindConfigurationError(Record.Exception(() => api.CreateClient()));

        Assert.Equal("setting 'Storage:Filesystem:Root': the directory does not exist", error.Message);
        Assert.DoesNotContain(_directory.Path, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StorageCheckIsPartOfReadiness()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        var report = await api.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Cancellation);

        var entry = report.Entries[StorageHealthCheck.Name];
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal("filesystem", entry.Data["provider"]);
        Assert.Equal("Healthy", await client.GetStringAsync(new Uri("/health", UriKind.Relative), Cancellation));
    }

    [Fact]
    public async Task DiagnosticsAreAbsentUnlessEnabled()
    {
        await using var api = Api();
        using var client = api.CreateClient();

        using var write = await client.PostAsync(new Uri("/diagnostics/storage/staging/probe/x", UriKind.Relative), new ByteArrayContent([1]), Cancellation);
        using var read = await client.GetAsync(new Uri("/diagnostics/storage/staging/probe/x?size=1&sha256=" + new string('a', 64), UriKind.Relative), Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(Root, "staging", "probe", "x")));
    }

    [Fact]
    public async Task DiagnosticsWriteOnceAndReadVerified()
    {
        await using var api = Api(("Storage:Diagnostics:Enabled", "true"));
        using var client = api.CreateClient();
        var body = RandomNumberGenerator.GetBytes(100_000);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(body));

        using var first = await client.PostAsync(new Uri("/diagnostics/storage/staging/probe/e2e/one", UriKind.Relative), new ByteArrayContent(body), Cancellation);
        using var second = await client.PostAsync(new Uri("/diagnostics/storage/staging/probe/e2e/one", UriKind.Relative), new ByteArrayContent([1, 2]), Cancellation);
        using var verified = await client.GetAsync(new Uri($"/diagnostics/storage/staging/probe/e2e/one?size={body.Length}&sha256={sha256}", UriKind.Relative), Cancellation);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var written = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Cancellation)))
        {
            Assert.Equal("staging/probe/e2e/one", written.RootElement.GetProperty("key").GetString());
            Assert.Equal(body.Length, written.RootElement.GetProperty("size").GetInt64());
            Assert.Equal(sha256, written.RootElement.GetProperty("sha256").GetString());
        }

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("AlreadyExists", (await second.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("outcome").GetString());
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.True((await verified.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("verified").GetBoolean());
        Assert.Equal(body, await File.ReadAllBytesAsync(Path.Combine(Root, "staging", "probe", "e2e", "one"), Cancellation));
    }

    [Fact]
    public async Task DiagnosticsReportMismatchesMissingObjectsAndRefusedKeysWithoutTheKey()
    {
        await using var api = Api(("Storage:Diagnostics:Enabled", "true"));
        using var client = api.CreateClient();
        using (await client.PostAsync(new Uri("/diagnostics/storage/staging/probe/e2e/two", UriKind.Relative), new ByteArrayContent([1, 2, 3]), Cancellation))
        {
        }

        var wrongHash = new string('0', 64);
        using var mismatch = await client.GetAsync(new Uri($"/diagnostics/storage/staging/probe/e2e/two?size=3&sha256={wrongHash}", UriKind.Relative), Cancellation);
        using var missing = await client.GetAsync(new Uri($"/diagnostics/storage/staging/probe/e2e/none?size=3&sha256={wrongHash}", UriKind.Relative), Cancellation);
        using var refusedWrite = await client.PostAsync(new Uri("/diagnostics/storage/Planted/Upper", UriKind.Relative), new ByteArrayContent([1]), Cancellation);
        using var refusedRead = await client.GetAsync(new Uri($"/diagnostics/storage/Planted/Upper?size=1&sha256={wrongHash}", UriKind.Relative), Cancellation);
        using var badQuery = await client.GetAsync(new Uri("/diagnostics/storage/staging/probe/e2e/two?size=-1&sha256=xyz", UriKind.Relative), Cancellation);
        using var keyIsDirectory = await client.PostAsync(new Uri("/diagnostics/storage/staging/probe", UriKind.Relative), new ByteArrayContent([1]), Cancellation);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        var integrity = await mismatch.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal("IntegrityFailure", integrity.GetProperty("outcome").GetString());
        Assert.Equal(StorageErrorCodes.HashMismatch, integrity.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refusedWrite.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refusedRead.StatusCode);
        var refused = await refusedWrite.Content.ReadAsStringAsync(Cancellation);
        Assert.Contains("a segment contains an upper-case letter", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("Planted", refused, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, badQuery.StatusCode);
        Assert.Contains("storage.invalid-expectation", await badQuery.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, keyIsDirectory.StatusCode);
        Assert.Contains(StorageErrorCodes.KeyIsDirectory, await keyIsDirectory.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> Api(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:Filesystem:Root", Root);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static ConfigurationError FindConfigurationError(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConfigurationError error)
            {
                return error;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            {
                return FindConfigurationError(aggregate.InnerExceptions[0]);
            }
        }

        throw new Xunit.Sdk.XunitException($"No configuration error was raised; got {exception?.GetType().FullName ?? "no exception"}.");
    }
}
