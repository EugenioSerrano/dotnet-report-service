using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Npgsql;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// The priority on the job contract over the running composition: the real API, the real
/// consumer registry file and the real PostgreSQL metadata database with every migration
/// applied. A consumer declares <c>low</c>, <c>normal</c> or <c>high</c>, or nothing, and the
/// job read back reports the declared priority and the effective class; an invalid level and a
/// <c>high</c> the deployment does not offer are refused synchronously and leave no job; one key
/// sent again answers by the idempotency rule; and the database itself refuses a change to the
/// declared priority of a stored job while the effective class stays a fact a later rule may move.
/// <para>
/// The uploads are seeded straight into the composition's store, verifying with a far deadline,
/// so the cases do not depend on the render path; the job creation and the read go through the
/// API, and the job counts and the guard are read from the database the API wrote to.
/// </para>
/// </summary>
public sealed class JobPriorityContractEndToEndTests
{
    private const string Consumer = "consumer-a";

    private const string OtherConsumer = "consumer-b";

    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    private const string CheckViolation = "23514";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    [Fact]
    public async Task EachLevelAndTheOmittedMemberAreAcceptedAndReadBackAsDeclaredAndEffective()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(restriction: null, cancellationToken);
        await using var dataSource = await DataSourceAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");

        foreach (var (sent, expected) in new (string?, string)[] { ("low", "low"), ("normal", "normal"), ("high", "high"), (null, "normal") })
        {
            var uploadId = await SeedUploadAsync(dataSource, Consumer, cancellationToken);
            using var response = await CreateAsync(api, Consumer, uploadId, sent is null ? null : Quoted(sent), null, cancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var reportJobId = (await AnswerAsync(response, cancellationToken)).GetProperty("reportJobId").GetString()!;

            using var read = await api.SendAsync(ReadRequest(reportJobId), cancellationToken);
            var priority = (await AnswerAsync(read, cancellationToken)).GetProperty("priority");
            Assert.Equal(expected, priority.GetProperty("declared").GetString());
            Assert.Equal(expected, priority.GetProperty("effective").GetString());
            Assert.Equal((expected, expected), await StoredPriorityAsync(dataSource, reportJobId, cancellationToken));
        }
    }

    [Fact]
    public async Task AnInvalidLevelIsRefusedSynchronouslyNamingTheFieldAndCreatesNoJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(restriction: null, cancellationToken);
        await using var dataSource = await DataSourceAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");
        var uploadId = await SeedUploadAsync(dataSource, Consumer, cancellationToken);

        foreach (var sent in new[] { Quoted("critical"), Quoted("urgent"), "null", "1", "{}" })
        {
            var before = await JobCountAsync(dataSource, cancellationToken);
            using var response = await CreateAsync(api, Consumer, uploadId, sent, null, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var problem = JsonDocument.Parse(body).RootElement;

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ErrorCodes.RequestInvalid, problem.GetProperty("code").GetString());
            Assert.Equal(CreateReportJobRequestReader.PriorityMember, problem.GetProperty("field").GetString());
            Assert.DoesNotContain("critical", body, StringComparison.Ordinal);
            Assert.DoesNotContain("urgent", body, StringComparison.Ordinal);
            Assert.Equal(before, await JobCountAsync(dataSource, cancellationToken));
        }
    }

    [Fact]
    public async Task TheSameKeyAnswersTheFirstJobForTheSamePriorityAndAConflictForAnother()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(restriction: null, cancellationToken);
        await using var dataSource = await DataSourceAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");
        var uploadId = await SeedUploadAsync(dataSource, Consumer, cancellationToken);
        var key = "priority-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        using var first = await CreateAsync(api, Consumer, uploadId, null, key, cancellationToken);
        using var same = await CreateAsync(api, Consumer, uploadId, Quoted("normal"), key, cancellationToken);
        using var other = await CreateAsync(api, Consumer, uploadId, Quoted("high"), key, cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, same.StatusCode);
        Assert.Equal(
            (await AnswerAsync(first, cancellationToken)).GetProperty("reportJobId").GetString(),
            (await AnswerAsync(same, cancellationToken)).GetProperty("reportJobId").GetString());
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal(ErrorCodes.IdempotencyConflict, (await AnswerAsync(other, cancellationToken)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheDatabaseRefusesAChangeOfTheDeclaredPriorityAndKeepsTheEffectiveClassMovable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(restriction: null, cancellationToken);
        await using var dataSource = await DataSourceAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");
        var uploadId = await SeedUploadAsync(dataSource, Consumer, cancellationToken);

        using var response = await CreateAsync(api, Consumer, uploadId, Quoted("low"), null, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var reportJobId = (await AnswerAsync(response, cancellationToken)).GetProperty("reportJobId").GetString()!;

        // The guard is the database's: an attempted update of the declared priority is refused.
        var refusal = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(dataSource, "UPDATE report_service.report_jobs SET declared_priority = 'high' WHERE report_job_id = @id", reportJobId, cancellationToken));
        Assert.Equal(CheckViolation, refusal.SqlState);
        Assert.Equal("low", (await StoredPriorityAsync(dataSource, reportJobId, cancellationToken)).Declared);

        // The effective class is a separate fact and stays movable.
        Assert.Equal(1, await ExecuteAsync(dataSource, "UPDATE report_service.report_jobs SET effective_priority_class = 'normal' WHERE report_job_id = @id", reportJobId, cancellationToken));
        Assert.Equal(("low", "normal"), await StoredPriorityAsync(dataSource, reportJobId, cancellationToken));

        using var read = await api.SendAsync(ReadRequest(reportJobId), cancellationToken);
        var priority = (await AnswerAsync(read, cancellationToken)).GetProperty("priority");
        Assert.Equal("low", priority.GetProperty("declared").GetString());
        Assert.Equal("normal", priority.GetProperty("effective").GetString());
    }

    [Fact]
    public async Task UnderAnAllowListAHighFromAnotherDeclaredConsumerIsRefusedWithNoJobAndNoPolicyDetail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(restriction: HighPriorityPolicy.AllowListRestriction, cancellationToken);
        await using var dataSource = await DataSourceAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");

        var allowed = await SeedUploadAsync(dataSource, Consumer, cancellationToken);
        using (var accepted = await CreateAsync(api, Consumer, allowed, Quoted("high"), null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        var outside = await SeedUploadAsync(dataSource, OtherConsumer, cancellationToken);
        var before = await JobCountAsync(dataSource, cancellationToken);
        using var refused = await CreateAsync(api, OtherConsumer, outside, Quoted("high"), null, cancellationToken);
        var body = await refused.Content.ReadAsStringAsync(cancellationToken);
        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedCapability, problem.GetProperty("code").GetString());
        Assert.Equal(CreateReportJobEndpoint.HighPriorityNotOfferedDetail, problem.GetProperty("detail").GetString());
        Assert.DoesNotContain(HighPriorityPolicy.AllowListRestriction, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Consumer, body, StringComparison.Ordinal);
        Assert.Equal(before, await JobCountAsync(dataSource, cancellationToken));

        using var normal = await CreateAsync(api, OtherConsumer, outside, Quoted("normal"), null, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, normal.StatusCode);
    }

    [Fact]
    public async Task WithHighDisabledEveryHighIsRefusedAndNothingIsCreated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(restriction: HighPriorityPolicy.DisabledRestriction, cancellationToken);
        await using var dataSource = await DataSourceAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");
        var uploadId = await SeedUploadAsync(dataSource, Consumer, cancellationToken);
        var before = await JobCountAsync(dataSource, cancellationToken);

        using var refused = await CreateAsync(api, Consumer, uploadId, Quoted("high"), null, cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedCapability, (await AnswerAsync(refused, cancellationToken)).GetProperty("code").GetString());
        Assert.Equal(before, await JobCountAsync(dataSource, cancellationToken));
    }

    // Starts the composition with a registry of two active consumers and, when asked, the API's
    // restriction of high: an allow-list naming the first consumer, or high disabled.
    private static async Task<DistributedApplication> StartAsync(string? restriction, CancellationToken cancellationToken)
    {
        var registry = Path.Combine(
            Path.GetTempPath(),
            "report-service-e2e-priority",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8],
            "consumers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        await File.WriteAllTextAsync(
            registry,
            """{ "consumers": [ { "consumerId": "consumer-a", "active": true }, { "consumerId": "consumer-b", "active": true } ] }""",
            cancellationToken);

        var builder = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");
        if (restriction is not null)
        {
            var apiResource = (ProjectResource)builder.Resources.Single(resource => resource.Name == "api");
            var section = HighPriorityPolicy.SectionPath.Replace(":", "__", StringComparison.Ordinal);
            var api = builder.CreateResourceBuilder(apiResource)
                .WithEnvironment(section + "__" + HighPriorityPolicy.RestrictionSetting, restriction);
            if (restriction == HighPriorityPolicy.AllowListRestriction)
            {
                api.WithEnvironment(section + "__" + HighPriorityPolicy.AllowedConsumersSetting + "__0", Consumer);
            }
        }

        var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using var client = app.CreateHttpClient("api");
        await Wait.UntilAsync(
            "the API answering /health",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            HealthyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return app;
    }

    private static async Task<NpgsqlDataSource> DataSourceAsync(DistributedApplication composition, CancellationToken cancellationToken)
    {
        var connectionString = await composition.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        return NpgsqlDataSource.Create(connectionString);
    }

    // A verifying upload of the consumer, with a far deadline: usable by a job request.
    private static async Task<string> SeedUploadAsync(NpgsqlDataSource dataSource, string consumer, CancellationToken cancellationToken)
    {
        var uploads = new PostgresUploadStore(dataSource);
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                now,
                now.AddHours(2)),
            cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, consumer, UploadContentEncoding.None, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(uploadId, UploadContentOutcome.Complete, 900, 800, Hash, now, cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, consumer, null, now, now, now.AddHours(2), cancellationToken));
        return uploadId;
    }

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient api,
        string consumer,
        string uploadId,
        string? priority,
        string? key,
        CancellationToken cancellationToken)
    {
        var member = priority is null ? string.Empty : $", \"priority\": {priority}";
        var body = $"{{ \"template\": {{ \"id\": \"{TestComposition.ByNameTemplate}\" }}, "
            + $"\"data\": {{ \"uploadId\": \"{uploadId}\", \"profile\": \"header-items-v1\" }}, "
            + $"\"outputs\": [ \"pdf\" ]{member} }}";
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, CreateReportJobRequestReader.JsonMediaType),
        };
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        if (key is not null)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return api.SendAsync(message, cancellationToken);
    }

    private static HttpRequestMessage ReadRequest(string reportJobId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}", UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return message;
    }

    private static string Quoted(string level) => "\"" + level + "\"";

    private static async Task<JsonElement> AnswerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<long> JobCountAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM report_service.report_jobs");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<(string Declared, string Effective)> StoredPriorityAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT declared_priority, effective_priority_class FROM report_service.report_jobs WHERE report_job_id = @id");
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<int> ExecuteAsync(NpgsqlDataSource dataSource, string sql, string reportJobId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
