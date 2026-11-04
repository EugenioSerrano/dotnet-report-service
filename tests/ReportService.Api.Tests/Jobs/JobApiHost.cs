using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The API on its real pipeline, with the job and upload records kept in memory, the
/// clock fixed and the consumer registry in a file the test writes: everything a job
/// acceptance decides — who may act, what the request asks for, and whether the
/// upload is usable at the instant the job commits — can then be asserted exactly.
/// </summary>
internal sealed class JobApiHost : IDisposable
{
    /// <summary>The instant the service starts at, with ticks below the millisecond.</summary>
    public static readonly DateTimeOffset Now =
        new DateTimeOffset(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero).AddTicks(7890);

    /// <summary>The consumer every case acts as.</summary>
    public const string Consumer = "dev-consumer";

    /// <summary>The consumer the registry carries as inactive.</summary>
    public const string InactiveConsumer = "consumer-c";

    /// <summary>The states an upload can be made in before a job is created against it.</summary>
    public enum UploadState
    {
        Created,
        Uploading,
        Verifying,
        VerifyingPastItsDeadline,
        Ready,
        Rejected,
        Expired,
    }

    private readonly TemporaryDirectory _directory = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _registryPath;
    private int _sequence;

    // The instant the service decides at, at the precision every stored row carries.
    private static readonly DateTimeOffset DecidesAt = JobWireValues.TruncateToMillisecond(Now);

    /// <summary>Starts the service over a registry carrying an active and an inactive consumer.</summary>
    /// <param name="store">Wraps the job records the service is given, so a case can place an event inside one call.</param>
    /// <param name="versions">Wraps the taken template versions the service reads, so a case can make the read fail.</param>
    public JobApiHost(
        Func<IJobStore, IJobStore>? store = null,
        Func<ITemplateVersionStore, ITemplateVersionStore>? versions = null)
    {
        _registryPath = _directory.Write(
            "consumers.json",
            "{ \"consumers\": ["
            + $"{{ \"consumerId\": \"{Consumer}\", \"active\": true }}, "
            + $"{{ \"consumerId\": \"{InactiveConsumer}\", \"active\": false }}"
            + "] }");
        Jobs = new InMemoryJobStore(Uploads);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, _registryPath);
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory.Path, "storage"));
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureTestServices(services =>
            {
                MetadataDatabaseStub.UseHealthy(services);
                services.RemoveAll<IUploadStore>();
                services.AddSingleton<IUploadStore>(Uploads);
                services.RemoveAll<IJobStore>();
                services.AddSingleton(store is null ? Jobs : store(Jobs));
                services.RemoveAll<ITemplateVersionStore>();
                services.AddSingleton<ITemplateVersionStore>(versions is null ? Versions : versions(Versions));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        });
        Client = _factory.CreateClient();
    }

    /// <summary>The upload records the service reads.</summary>
    public InMemoryUploadStore Uploads { get; } = new();

    /// <summary>The job records the service writes.</summary>
    public InMemoryJobStore Jobs { get; }

    /// <summary>The taken template versions the service reads when a job is created.</summary>
    public InMemoryTemplateVersionStore Versions { get; } = new();

    /// <summary>The clock the service reads.</summary>
    public FixedTimeProvider Clock { get; } = new(Now);

    /// <summary>Every line the service logged.</summary>
    public LogCapture Logs { get; } = new();

    /// <summary>The client the cases send with.</summary>
    public HttpClient Client { get; }

    /// <summary>Records an upload in <paramref name="state"/> through the store's own steps.</summary>
    /// <param name="state">The state the upload must be in when it is returned.</param>
    /// <param name="consumerId">The owner of the upload.</param>
    /// <returns>The identifier of the upload.</returns>
    public async Task<string> UploadAsync(UploadState state, string consumerId = Consumer)
    {
        var uploadId = UploadIdentifier.Prefix + (++_sequence).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
        var record = new UploadRecord(
            uploadId,
            consumerId,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            ObjectKey.FromSegments("staging", uploadId, "content"),
            Now.AddHours(-1),
            Now.AddHours(1));
        await Uploads.CreateAsync(record, TestContext.Current.CancellationToken);
        if (state is UploadState.Created)
        {
            return uploadId;
        }

        var started = Now.AddMinutes(-30);
        await Uploads.TryStartTransferAsync(uploadId, consumerId, UploadContentEncoding.None, started, TestContext.Current.CancellationToken);
        if (state is UploadState.Uploading)
        {
            return uploadId;
        }

        if (state is UploadState.Rejected)
        {
            await Uploads.RejectTransferAsync(
                uploadId,
                UploadRejectionCode.DataInvalid,
                UploadRejectionReason.Truncated,
                12,
                Now.AddMinutes(-20),
                TestContext.Current.CancellationToken);
            return uploadId;
        }

        if (state is UploadState.Expired)
        {
            await Uploads.ExpireIfOverdueAsync(uploadId, record.ExpiresAt, TestContext.Current.CancellationToken);
            return uploadId;
        }

        await Uploads.RecordTransferAsync(
            uploadId,
            UploadContentOutcome.Complete,
            900,
            800,
            "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3",
            Now.AddMinutes(-20),
            TestContext.Current.CancellationToken);
        var deadline = state is UploadState.VerifyingPastItsDeadline ? DecidesAt : DecidesAt.AddHours(5);
        await Uploads.ConfirmAsync(
            uploadId,
            consumerId,
            null,
            Now.AddMinutes(-21),
            Now.AddMinutes(-20),
            deadline,
            TestContext.Current.CancellationToken);

        if (state is UploadState.Ready)
        {
            await Uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    consumerId,
                    ObjectKey.FromSegments("snapshots", uploadId, "a"),
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3",
                    900,
                    "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3",
                    900,
                    Now.AddMinutes(-10)),
                Now.AddMinutes(-10),
                TestContext.Current.CancellationToken);
        }

        return uploadId;
    }

    /// <summary>The document that creates a job over <paramref name="uploadId"/>.</summary>
    /// <param name="uploadId">The upload the job reports on.</param>
    /// <param name="version">The version the reference names; null for a reference by name.</param>
    /// <param name="profile">The declared data profile.</param>
    /// <param name="outputs">The outputs, as the JSON array's elements.</param>
    /// <param name="templateId">The template's identifier.</param>
    public static string Body(
        string uploadId,
        string? version = "1.0.0",
        string profile = UploadWireValues.HeaderItemsV1,
        string outputs = "\"pdf\"",
        string templateId = "pilot-grouped")
    {
        var reference = version is null
            ? $"\"id\": \"{templateId}\""
            : $"\"id\": \"{templateId}\", \"version\": \"{version}\"";
        return $"{{ \"template\": {{ {reference} }}, "
            + $"\"data\": {{ \"uploadId\": \"{uploadId}\", \"profile\": \"{profile}\" }}, "
            + $"\"outputs\": [ {outputs} ] }}";
    }

    /// <summary>The request that creates a job, declared as <paramref name="consumerId"/>.</summary>
    /// <param name="consumerId">The declared consumer; null sends no identity at all.</param>
    /// <param name="body">The document to send.</param>
    public static HttpRequestMessage CreateRequest(string? consumerId, string body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, CreateReportJobRequestReader.JsonMediaType),
        };
        Declare(message, consumerId);
        return message;
    }

    /// <summary>The problem document of <paramref name="response"/>, without its trace identifier.</summary>
    /// <param name="response">The answer.</param>
    public static async Task<string> ProblemAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return string.Join(
            ';',
            body.RootElement.EnumerateObject()
                .Where(member => member.Name != "traceId")
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .Select(member => member.Name + "=" + member.Value.GetRawText()));
    }

    /// <summary>The members of a successful answer.</summary>
    /// <param name="response">The answer.</param>
    public static async Task<JsonElement> AnswerAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _directory.Dispose();
        Logs.Dispose();
    }

    private static void Declare(HttpRequestMessage message, string? consumerId)
    {
        if (consumerId is not null)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumerId);
        }
    }
}
