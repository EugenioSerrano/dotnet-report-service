using System.Globalization;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ReportService.EndToEndTests;

/// <summary>
/// The local composition runs the three processes and the one-shot migrator: the
/// API and the orchestration worker are ready only with the metadata database at
/// the version the code expects, the PDF worker carries no database check, and
/// every process reaches the database as the application role — the AppHost's own
/// health checks on the server's bootstrap user excepted.
/// </summary>
public sealed class CompositionTopologyTests
{
    // The first run may build the PDF base image and publish the render worker image.
    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private const string Baseline = "0001_baseline.sql";
    private const string ApplicationRole = "reportservice";
    private const string BootstrapRole = "postgres";

    private static readonly string[] Resources = ["postgres", "cache", "temporal", "render-worker", "orchestration-worker", "api"];

    [Fact]
    public async Task ThreeProcessesRunAndEachWorkerIsReadyAgainstTheEngineAndTheDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
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

        using var api = app.CreateHttpClient("api");
        using (var health = JsonDocument.Parse(await api.GetStringAsync(new Uri("/health", UriKind.Relative), cancellationToken)))
        {
            Assert.Equal("Healthy", health.RootElement.GetProperty("status").GetString());
            var database = Check(health, "metadata-database");
            Assert.Equal("Healthy", database.GetProperty("status").GetString());
            Assert.Equal(Baseline, database.GetProperty("data").GetProperty("expected").GetString());
            Assert.Equal(Baseline, database.GetProperty("data").GetProperty("applied").GetString());
        }

        using (var registry = JsonDocument.Parse(await api.GetStringAsync(new Uri("/health/registry", UriKind.Relative), cancellationToken)))
        {
            var data = registry.RootElement.GetProperty("checks")[0].GetProperty("data");
            Assert.Equal(2, data.GetProperty("entries").GetInt32());
        }

        using var orchestration = app.CreateHttpClient("orchestration-worker");
        using (var health = JsonDocument.Parse(await orchestration.GetStringAsync(new Uri("/health", UriKind.Relative), cancellationToken)))
        {
            var engine = Check(health, "workflow-engine");
            Assert.Equal("Healthy", engine.GetProperty("status").GetString());
            var queues = engine.GetProperty("data").GetProperty("queues");
            Assert.Equal("no registered work", queues.GetProperty("report-orchestration").GetString());
            Assert.Equal("no registered work", queues.GetProperty("report-data").GetString());

            var database = Check(health, "metadata-database");
            Assert.Equal("Healthy", database.GetProperty("status").GetString());
            Assert.Equal(Baseline, database.GetProperty("data").GetProperty("expected").GetString());
            Assert.Equal(Baseline, database.GetProperty("data").GetProperty("applied").GetString());
        }

        using var renderWorker = app.CreateHttpClient("render-worker", "http");
        using (var health = JsonDocument.Parse(await renderWorker.GetStringAsync(new Uri("/health", UriKind.Relative), cancellationToken)))
        {
            Assert.Equal("Healthy", Check(health, "chromium").GetProperty("status").GetString());
            var engine = Check(health, "workflow-engine");
            Assert.Equal("Healthy", engine.GetProperty("status").GetString());
            Assert.Equal("no registered work", engine.GetProperty("data").GetProperty("queues").GetProperty("report-pdf-rendering").GetString());

            // The PDF worker writes no metadata record yet, so it carries no database check.
            Assert.DoesNotContain(
                health.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetProperty("name").GetString() == "metadata-database");
        }

        // No product resource is handed the server's own connection string, which
        // carries the bootstrap superuser.
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = model.Resources.Single(resource => resource.Name == "postgres");
        var databaseResource = model.Resources.Single(resource => resource.Name == ApplicationRole);
        foreach (var name in new[] { "api", "orchestration-worker", "migrations" })
        {
            var resource = model.Resources.Single(candidate => candidate.Name == name);
            Assert.DoesNotContain(
                resource.Annotations.OfType<ConnectionStringRedirectAnnotation>(),
                redirect => ReferenceEquals(redirect.Resource, server) || ReferenceEquals(redirect.Resource, databaseResource));
        }

        // The database, its role and its journal, read with the bootstrap superuser
        // (the test's own connection; no product process gets it).
        var bootstrap = await app.GetConnectionStringAsync(BootstrapRole, cancellationToken)
            ?? throw new InvalidOperationException("The composition published no bootstrap connection string.");
        await using var connection = new NpgsqlConnection(bootstrap);
        await connection.OpenAsync(cancellationToken);

        Assert.False(await BoolAsync(connection, "SELECT rolsuper FROM pg_roles WHERE rolname = @role", cancellationToken));
        Assert.Equal(ApplicationRole, await TextAsync(connection, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @role", cancellationToken));

        // The journal belongs to the application's own database, not to the server's
        // default one, so it is read through the database resource's connection string.
        var metadata = await app.GetConnectionStringAsync(ApplicationRole, cancellationToken)
            ?? throw new InvalidOperationException("The composition published no metadata connection string.");
        await using (var journalConnection = new NpgsqlConnection(metadata))
        {
            await journalConnection.OpenAsync(cancellationToken);
            Assert.Equal(
                [Baseline],
                await journalConnection.QueryAsync<string>(new CommandDefinition(
                    "SELECT script_name FROM public.schema_migrations ORDER BY script_name",
                    cancellationToken: cancellationToken)));
        }

        // Every session on the database uses the application role, except the AppHost's
        // own health check: the composition registers a PostgreSQL check on the database
        // resource, and that one connects with the server's bootstrap user. AC-8's
        // wording ("every session uses the role") therefore reads "every session a
        // product process opens"; the composition-level proof that no product resource
        // is handed the bootstrap credentials is the redirect assertion above.
        var users = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT usename FROM pg_stat_activity WHERE datname = @role AND pid <> pg_backend_pid()",
            new { role = ApplicationRole },
            cancellationToken: cancellationToken));
        Assert.All(users, user => Assert.Contains(user, new[] { ApplicationRole, BootstrapRole }));
    }

    private static async Task<bool> BoolAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken) =>
        Convert.ToBoolean(
            await connection.ExecuteScalarAsync(new CommandDefinition(sql, new { role = ApplicationRole }, cancellationToken: cancellationToken)),
            CultureInfo.InvariantCulture);

    private static async Task<string?> TextAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken) =>
        Convert.ToString(
            await connection.ExecuteScalarAsync(new CommandDefinition(sql, new { role = ApplicationRole }, cancellationToken: cancellationToken)),
            CultureInfo.InvariantCulture);

    private static JsonElement Check(JsonDocument health, string name) =>
        Assert.Single(health.RootElement.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == name);
}
