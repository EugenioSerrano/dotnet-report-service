using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using ReportService.Api.Http;
using ReportService.Api.Uploads;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// Opening an upload session on the running composition: a registered, active
/// consumer creates one and receives its identifier, its state and the time it
/// expires; it reads the same status back from the address it was given; and the
/// same consumer asking for direct storage is refused at once.
/// </summary>
public sealed class UploadSessionEndToEndTests
{
    private const string Consumer = "dev-consumer";
    private const string ValidBody = """{"profile":"header-items-v1","mode":"API_STREAM"}""";
    private const string DirectStorageBody = """{"profile":"header-items-v1","mode":"DIRECT_STORAGE"}""";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    [Fact]
    public async Task ARegisteredConsumerOpensASessionReadsItBackAndDirectStorageIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await TestComposition.CreateAsync(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        }

        using var api = app.CreateHttpClient("api");

        using var created = await api.SendAsync(CreateRequest(ValidBody), cancellationToken);
        var createdBody = await created.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var projection = JsonDocument.Parse(createdBody);
        var root = projection.RootElement;
        var uploadId = root.GetProperty("uploadId").GetString();
        Assert.NotNull(uploadId);
        Assert.Matches("^upload-[0-9a-f]{32}$", uploadId);
        Assert.Equal("created", root.GetProperty("status").GetString());
        Assert.Equal("API_STREAM", root.GetProperty("mode").GetString());
        Assert.Equal("header-items-v1", root.GetProperty("profile").GetString());
        Assert.Equal(TimeSpan.FromHours(24), Instant(root, "expiresAt") - Instant(root, "createdAt"));

        var location = created.Headers.Location;
        Assert.NotNull(location);
        Assert.Equal($"{UploadEndpoints.RoutePrefix}/{uploadId}", location.OriginalString);

        using var read = await api.SendAsync(GetRequest(location.OriginalString), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(createdBody, await read.Content.ReadAsStringAsync(cancellationToken));

        using var refused = await api.SendAsync(CreateRequest(DirectStorageBody), cancellationToken);
        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(cancellationToken)).RootElement;
        Assert.Equal(422, (int)refused.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedCapability, problem.GetProperty("code").GetString());
    }

    private static DateTimeOffset Instant(JsonElement root, string member) => DateTimeOffset.Parse(
        root.GetProperty(member).GetString()!,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static HttpRequestMessage CreateRequest(string body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return message;
    }

    private static HttpRequestMessage GetRequest(string path)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return message;
    }
}
