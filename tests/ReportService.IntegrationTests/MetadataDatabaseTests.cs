using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.IntegrationTests;

/// <summary>
/// The metadata database against the real pinned server, initialised by the
/// repository's own application-role script: the role is a non-superuser that
/// owns its database; the runner reaches the current version, changes nothing on
/// a second run, applies only a new script on an upgrade, rolls a failing script
/// back, refuses a changed or unknown entry, serialises two runners, and the
/// readiness check follows the real journal through an outage and its recovery.
/// </summary>
public sealed class MetadataDatabaseTests(PostgresFixture server) : IClassFixture<PostgresFixture>
{
    private const string Baseline = "0001_baseline.sql";
    private const string Role = PostgresFixture.ApplicationRole;
    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotReadyDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TheInitScriptCreatesANonSuperuserRoleThatOwnsItsDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(server.Bootstrap());
        await connection.OpenAsync(cancellationToken);

        Assert.False(await BoolAsync(connection, "SELECT rolsuper FROM pg_roles WHERE rolname = @role", cancellationToken));
        Assert.False(await BoolAsync(connection, "SELECT rolcreatedb FROM pg_roles WHERE rolname = @role", cancellationToken));
        Assert.False(await BoolAsync(connection, "SELECT rolcreaterole FROM pg_roles WHERE rolname = @role", cancellationToken));
        Assert.Equal(Role, await TextAsync(connection, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @role", cancellationToken));

        await using var asRole = new NpgsqlConnection(server.Application(Role));
        await asRole.OpenAsync(cancellationToken);
        var refused = await Assert.ThrowsAsync<PostgresException>(async () =>
            await asRole.ExecuteAsync(new CommandDefinition("CREATE DATABASE metadata_it_must_not_exist", cancellationToken: cancellationToken)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task AFreshDatabaseReachesTheCurrentVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await server.CreateDatabaseAsync(cancellationToken);

        var result = await MigrationRunner.RunAsync(server.Application(database), MigrationCatalog.Scripts, cancellationToken);
        assertNoCredential(result);

        Assert.True(result.Succeeded, string.Join("; ", result.Lines()));
        Assert.Equal([Baseline], result.Applied);
        Assert.Equal(Baseline, result.CurrentVersion);
        Assert.Empty(result.AlreadyApplied);

        await using var connection = await server.OpenAsync(database, cancellationToken);
        var row = await connection.QuerySingleAsync<JournalSnapshot>(new CommandDefinition(
            "SELECT script_name AS \"ScriptName\", checksum AS \"Checksum\", applied_at AS \"AppliedAt\" FROM public.schema_migrations",
            cancellationToken: cancellationToken));
        Assert.Equal(Baseline, row.ScriptName);
        Assert.Equal(MigrationCatalog.Scripts[0].Checksum, row.Checksum);
        Assert.NotEqual(default, row.AppliedAt);
        Assert.Equal(Role, await TextAsync(connection, "SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'report_service'", cancellationToken));
    }

    [Fact]
    public async Task ASecondRunAppliesNothingAndLeavesTheJournalUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await server.CreateDatabaseAsync(cancellationToken);
        await MigrationRunner.RunAsync(server.Application(database), MigrationCatalog.Scripts, cancellationToken);

        var before = await JournalAsync(database, cancellationToken);
        var second = await MigrationRunner.RunAsync(server.Application(database), MigrationCatalog.Scripts, cancellationToken);
        assertNoCredential(second);
        var after = await JournalAsync(database, cancellationToken);

        Assert.True(second.Succeeded, string.Join("; ", second.Lines()));
        Assert.Empty(second.Applied);
        Assert.Contains("no change; schema is current at " + Baseline, second.Lines().Single(), StringComparison.Ordinal);

        // The snapshots are compared by value: two journal rows are the same entry
        // when their script, checksum and timestamp agree, never when the objects do.
        Assert.Equal(
            before.Select(row => (row.ScriptName, row.Checksum, row.AppliedAt)),
            after.Select(row => (row.ScriptName, row.Checksum, row.AppliedAt)));
    }

    [Fact]
    public async Task AnUpgradeAppliesOnlyTheNewScript()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await server.CreateDatabaseAsync(cancellationToken);
        await MigrationRunner.RunAsync(server.Application(database), MigrationCatalog.Scripts, cancellationToken);
        var second = Script("0002_test-table.sql", "CREATE TABLE report_service.it_upgrade (id int PRIMARY KEY);");

        var result = await MigrationRunner.RunAsync(server.Application(database), [.. MigrationCatalog.Scripts, second], cancellationToken);
        assertNoCredential(result);

        Assert.True(result.Succeeded, string.Join("; ", result.Lines()));
        Assert.Equal(["0002_test-table.sql"], result.Applied);
        Assert.Equal("0002_test-table.sql", result.CurrentVersion);
        Assert.Equal(
            [Baseline, "0002_test-table.sql"],
            (await JournalAsync(database, cancellationToken)).Select(row => row.ScriptName));
    }

    [Fact]
    public async Task AFailingScriptLeavesNoChange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await server.CreateDatabaseAsync(cancellationToken);
        var failing = Script(
            "0002_failing.sql",
            "CREATE TABLE report_service.it_failing (id int);\nSELECT * FROM report_service.it_missing;");

        var result = await MigrationRunner.RunAsync(server.Application(database), [.. MigrationCatalog.Scripts, failing], cancellationToken);
        assertNoCredential(result);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Equal("0002_failing.sql", result.Failure.ScriptName);
        Assert.True(result.Failure.Position > 0, $"position {result.Failure.Position}");
        Assert.Equal(PostgresErrorCodes.UndefinedTable, result.Failure.SqlState);
        Assert.Contains(
            "migration '0002_failing.sql' failed at position",
            result.Lines().Single(line => line.Contains("failed", StringComparison.Ordinal)),
            StringComparison.Ordinal);

        await using var connection = await server.OpenAsync(database, cancellationToken);
        Assert.Equal(0m, await NumberAsync(connection, "SELECT COUNT(*) FROM pg_tables WHERE tablename = 'it_failing'", cancellationToken));
        Assert.Equal([Baseline], (await JournalAsync(database, cancellationToken)).Select(row => row.ScriptName));
    }

    [Fact]
    public async Task AChangedScriptIsRefusedAndNothingIsApplied()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await server.CreateDatabaseAsync(cancellationToken);
        await using var connection = await server.OpenAsync(database, cancellationToken);
        await MigrationJournal.EnsureAsync(connection, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO public.schema_migrations (script_name, checksum) VALUES (@name, @checksum)",
            new { name = Baseline, checksum = new string('0', 64) },
            cancellationToken: cancellationToken));

        var result = await MigrationRunner.RunAsync(server.Application(database), MigrationCatalog.Scripts, cancellationToken);
        assertNoCredential(result);

        Assert.False(result.Succeeded);
        Assert.Equal(["migration '" + Baseline + "' changed after it was applied"], result.Refusals);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public async Task AnUnknownAppliedVersionIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await server.CreateDatabaseAsync(cancellationToken);
        await using var connection = await server.OpenAsync(database, cancellationToken);
        await MigrationJournal.EnsureAsync(connection, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO public.schema_migrations (script_name, checksum) VALUES (@name, @checksum)",
            new { name = "9999_unknown.sql", checksum = new string('a', 64) },
            cancellationToken: cancellationToken));

        var result = await MigrationRunner.RunAsync(server.Application(database), MigrationCatalog.Scripts, cancellationToken);
        assertNoCredential(result);

        Assert.False(result.Succeeded);
        Assert.Equal(["unknown applied migration '9999_unknown.sql'"], result.Refusals);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public async Task TwoConcurrentRunnersApplyEachScriptOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await server.CreateDatabaseAsync(cancellationToken);
        var connectionString = server.Application(database);
        using var barrier = new Barrier(2);

        async Task<MigrationResult> RunAsync()
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            return await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        }

        var runners = new[]
        {
            Task.Run(RunAsync, cancellationToken),
            Task.Run(RunAsync, cancellationToken),
        };
        var results = await Task.WhenAll(runners);
        foreach (var result in results)
        {
            assertNoCredential(result);
        }

        Assert.All(results, result => Assert.True(result.Succeeded, string.Join("; ", result.Lines())));
        Assert.Single(results, result => result.Applied.Count == 1);
        Assert.Single(results, result => result.Applied.Count == 0);
        Assert.Single(await JournalAsync(database, cancellationToken));
    }

    [Fact]
    public async Task ReadinessReflectsTheRealJournal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await server.CreateDatabaseAsync(cancellationToken);
        var connectionString = server.Application(database);
        var check = Check(connectionString, Baseline);

        var before = await check.CheckHealthAsync(Context(), cancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, before.Status);
        Assert.Equal("none", before.Data["applied"]);

        await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        var current = await check.CheckHealthAsync(Context(), cancellationToken);
        Assert.Equal(HealthStatus.Healthy, current.Status);
        Assert.Equal(Baseline, current.Data["expected"]);
        Assert.Equal(Baseline, current.Data["applied"]);

        await using (var connection = await server.OpenAsync(database, cancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO public.schema_migrations (script_name, checksum) VALUES (@name, @checksum)",
                new { name = "0002_ahead.sql", checksum = new string('b', 64) },
                cancellationToken: cancellationToken));
        }

        var ahead = await check.CheckHealthAsync(Context(), cancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, ahead.Status);
        Assert.Equal("0002_ahead.sql", ahead.Data["applied"]);
        Assert.DoesNotContain(server.RolePassword, ahead.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOutageIsReportedAndTheSameProcessRecovers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await server.CreateDatabaseAsync(cancellationToken);
        await MigrationRunner.RunAsync(server.Application(database), MigrationCatalog.Scripts, cancellationToken);

        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, server.Application(database));
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, Baseline);
        });
        using var client = api.CreateClient();

        await WaitForAsync(
            "/health answering 200 before the outage",
            async token => (await client.GetAsync(new Uri("/health", UriKind.Relative), token)).StatusCode == HttpStatusCode.OK,
            ReadyDeadline,
            cancellationToken);

        try
        {
            await server.PauseAsync(cancellationToken);
            var body = await WaitForAsync(
                "/health answering 503 with an unreachable database",
                async token =>
                {
                    using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                    if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                    {
                        return null;
                    }

                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    var data = json.RootElement.GetProperty("checks")[0].GetProperty("data");
                    return data.TryGetProperty("error", out var error) && error.GetString() == "unreachable"
                        ? json.RootElement.GetRawText()
                        : null;
                },
                NotReadyDeadline,
                cancellationToken);
            Assert.Equal("Unhealthy", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());
            Assert.DoesNotContain(server.RolePassword, body, StringComparison.Ordinal);
        }
        finally
        {
            await server.UnpauseAsync(cancellationToken);
        }

        await WaitForAsync(
            "/health answering 200 again on the same process",
            async token => (await client.GetAsync(new Uri("/health", UriKind.Relative), token)).StatusCode == HttpStatusCode.OK,
            ReadyDeadline,
            cancellationToken);
    }

    [Fact]
    public async Task NoCredentialReachesTheOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await server.CreateDatabaseAsync(cancellationToken);
        var result = await MigrationRunner.RunAsync(server.Application(database), MigrationCatalog.Scripts, cancellationToken);
        var health = await Check(server.Application(database), Baseline).CheckHealthAsync(Context(), cancellationToken);
        var text = string.Join("\n", result.Lines())
            + "\n" + health.Description
            + "\n" + string.Join("\n", health.Data.Select(entry => $"{entry.Key}={entry.Value}"));

        Assert.True(result.Succeeded, string.Join("; ", result.Lines()));
        Assert.DoesNotContain(server.RolePassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(server.BootstrapPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
    }

    private void assertNoCredential(MigrationResult result)
    {
        var text = string.Join("\n", result.Lines());
        Assert.DoesNotContain(server.RolePassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(server.BootstrapPassword, text, StringComparison.Ordinal);
    }

    private static DatabaseHealthCheck Check(string connectionString, string expected) =>
        new(
            new NpgsqlSchemaJournalReader(connectionString),
            Options.Create(new DatabaseOptions { ConnectionString = connectionString, ExpectedSchemaVersion = expected }),
            NullLogger<DatabaseHealthCheck>.Instance);

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration("metadata-database", new NoopCheck(), failureStatus: null, tags: null),
    };

    private static MigrationScript Script(string name, string sql) =>
        new(name, sql, MigrationScript.ChecksumOf(Encoding.UTF8.GetBytes(sql)));

    private static async Task<bool> BoolAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken) =>
        Convert.ToBoolean(
            await connection.ExecuteScalarAsync(new CommandDefinition(sql, new { role = Role }, cancellationToken: cancellationToken)),
            CultureInfo.InvariantCulture);

    private static async Task<decimal> NumberAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken) =>
        Convert.ToDecimal(
            await connection.ExecuteScalarAsync(new CommandDefinition(sql, cancellationToken: cancellationToken)),
            CultureInfo.InvariantCulture);

    private static async Task<string?> TextAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken) =>
        Convert.ToString(
            await connection.ExecuteScalarAsync(new CommandDefinition(sql, new { role = Role }, cancellationToken: cancellationToken)),
            CultureInfo.InvariantCulture);

    private async Task<IReadOnlyList<JournalSnapshot>> JournalAsync(string database, CancellationToken cancellationToken)
    {
        await using var connection = await server.OpenAsync(database, cancellationToken);
        return [.. await connection.QueryAsync<JournalSnapshot>(new CommandDefinition(
            "SELECT script_name AS \"ScriptName\", checksum AS \"Checksum\", applied_at AS \"AppliedAt\" FROM public.schema_migrations ORDER BY script_name",
            cancellationToken: cancellationToken))];
    }

    /// <summary>Polls a readiness probe until it answers, never an assertion, and names what was awaited at the deadline.</summary>
    private static async Task<T> WaitForAsync<T>(
        string what,
        Func<CancellationToken, Task<T?>> probe,
        TimeSpan deadline,
        CancellationToken cancellationToken)
        where T : class
    {
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await probe(cancellationToken) is { } answer)
            {
                return answer;
            }

            if (DateTimeOffset.UtcNow - started >= deadline)
            {
                throw new TimeoutException($"{what} did not answer within {deadline.TotalSeconds:F0} s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    private static Task WaitForAsync(
        string what,
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan deadline,
        CancellationToken cancellationToken) =>
        WaitForAsync<object>(
            what,
            async token => await probe(token) ? new object() : null,
            deadline,
            cancellationToken);

    private sealed class JournalSnapshot
    {
        public string? ScriptName { get; set; }

        public string? Checksum { get; set; }

        public DateTimeOffset AppliedAt { get; set; }
    }

    private sealed class NoopCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Healthy());
    }
}
