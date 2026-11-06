using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReportService.Migrations;

namespace ReportService.EndToEndTests;

/// <summary>
/// The local composition runs the three processes and the one-shot migrator: the
/// API and the orchestration worker are ready only with the metadata database at
/// the version the code expects, the PDF worker carries no database check, and
/// every process reaches the database as the application role — the AppHost's own
/// health checks on the server's bootstrap user excepted. The orchestration worker
/// and the render worker each run one engine worker per queue they register work
/// on, so every queue they serve reports a running worker.
/// </summary>
public sealed class CompositionTopologyTests
{
    // The first run may build the PDF base image and publish the render worker image.
    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    // How often a queue's reported state is read while the composition settles.
    private static readonly TimeSpan QueueReadInterval = TimeSpan.FromMilliseconds(250);

    private const string WorkerRunning = "worker running";

    private const string WorkerStopped = "worker stopped";

    private const string ApplicationRole = "reportservice";
    private const string BootstrapRole = "postgres";

    // The version the code expects and the scripts an empty database must reach: read
    // from the shipped migration set, exactly as the composition reads them, so a
    // migration added later changes the expectation with the code instead of here.
    private static readonly string ExpectedVersion = MigrationCatalog.ExpectedVersion;
    private static readonly string[] AppliedScripts = [.. MigrationCatalog.Scripts.Select(script => script.Name)];

    private static readonly string[] Resources = ["postgres", "cache", "temporal", "render-worker", "orchestration-worker", "api"];

    /// <summary>One queue of a worker and the state the work registered on it makes it report.</summary>
    /// <param name="Worker">The process that declares the queue.</param>
    /// <param name="Queue">The queue's name.</param>
    /// <param name="State">The state the registered work makes the queue's health report.</param>
    private sealed record QueueExpectation(string Worker, string Queue, string State);

    /// <summary>
    /// The state each worker reports for each of its queues, in one place: the
    /// orchestration worker registers the job workflow and its metadata steps on
    /// <c>report-orchestration</c> and the verification and partitioning activities on
    /// <c>report-data</c>, and the render worker registers the template take and the
    /// by-name resolution on <c>report-pdf-rendering</c>, so every queue reports a
    /// running worker. Stated together, a queue that stops being declared fails by name
    /// rather than disappearing; a change that registers work on a queue updates this
    /// table in the same change.
    /// </summary>
    private static readonly QueueExpectation[] QueueExpectations =
    [
        new("orchestration-worker", "report-orchestration", WorkerRunning),
        new("orchestration-worker", "report-data", WorkerRunning),
        new("render-worker", "report-pdf-rendering", WorkerRunning),
    ];

    [Fact]
    public async Task ThreeProcessesRunAndEachWorkerIsReadyAgainstTheEngineAndTheDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await TestComposition.CreateAsync(cancellationToken);
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
            Assert.Equal(ExpectedVersion, database.GetProperty("data").GetProperty("expected").GetString());
            Assert.Equal(ExpectedVersion, database.GetProperty("data").GetProperty("applied").GetString());
        }

        using (var registry = JsonDocument.Parse(await api.GetStringAsync(new Uri("/health/registry", UriKind.Relative), cancellationToken)))
        {
            var data = registry.RootElement.GetProperty("checks")[0].GetProperty("data");
            Assert.Equal(2, data.GetProperty("entries").GetInt32());
        }

        using var orchestration = app.CreateHttpClient("orchestration-worker");
        using (var health = await AwaitWorkerHealthAsync(orchestration, "orchestration-worker", cancellationToken))
        {
            var engine = Check(health, "workflow-engine");
            Assert.Equal("Healthy", engine.GetProperty("status").GetString());

            var database = Check(health, "metadata-database");
            Assert.Equal("Healthy", database.GetProperty("status").GetString());
            Assert.Equal(ExpectedVersion, database.GetProperty("data").GetProperty("expected").GetString());
            Assert.Equal(ExpectedVersion, database.GetProperty("data").GetProperty("applied").GetString());
        }

        using var renderWorker = app.CreateHttpClient("render-worker", "http");
        using (var health = await AwaitWorkerHealthAsync(renderWorker, "render-worker", cancellationToken))
        {
            Assert.Equal("Healthy", Check(health, "chromium").GetProperty("status").GetString());
            var engine = Check(health, "workflow-engine");
            Assert.Equal("Healthy", engine.GetProperty("status").GetString());
            Assert.Equal("worker running", engine.GetProperty("data").GetProperty("queues").GetProperty("report-pdf-rendering").GetString());

            // The worker serves the take, which publishes a record, so it carries the
            // metadata database check and needs the migrations to have run.
            Assert.Contains(
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
                AppliedScripts,
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

    /// <summary>
    /// Reads the worker's health until every queue it declares reports the state its
    /// registered work makes it report, and answers with the body that was read. Readiness
    /// is published a moment before the worker's engine worker exists, so a queue's state
    /// is awaited within the class's deadline and then asserted, never merely awaited: the
    /// failure names the queue, the expected value, the observed value and the elapsed
    /// time, and a queue whose worker stopped fails by name instead of being waited out. An
    /// answer that is not readable — the endpoint answers 503 while one of its own checks
    /// is failing — is not ready either, so the wait goes on and keeps the last error for
    /// the failure.
    /// </summary>
    private static async Task<JsonDocument> AwaitWorkerHealthAsync(
        HttpClient worker,
        string workerName,
        CancellationToken cancellationToken)
    {
        var expected = QueueExpectations
            .Where(row => string.Equals(row.Worker, workerName, StringComparison.Ordinal))
            .ToArray();
        var elapsed = Stopwatch.StartNew();
        var lastReport = "no readable queue state";
        string? lastError = null;
        while (true)
        {
            using (var response = await worker.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken))
            {
                if (response.IsSuccessStatusCode)
                {
                    var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    var queues = Check(document, "workflow-engine").GetProperty("data").GetProperty("queues");
                    var reported = expected
                        .Select(row => (Row: row, Observed: queues.TryGetProperty(row.Queue, out var state) ? state.GetString() : null))
                        .ToArray();
                    var pending = reported
                        .Where(pair => !string.Equals(pair.Observed, pair.Row.State, StringComparison.Ordinal))
                        .ToArray();

                    if (pending.Length == 0)
                    {
                        // The wait returned because every queue reports what its registered work
                        // makes it report; the assertion states that expectation here.
                        foreach (var pair in reported)
                        {
                            Assert.Equal(pair.Row.State, pair.Observed);
                        }

                        return document;
                    }

                    document.Dispose();
                    lastError = null;
                    lastReport = string.Join(
                        "; ",
                        pending.Select(pair => $"'{pair.Row.Queue}' reported '{pair.Observed ?? "nothing"}' where '{pair.Row.State}' was expected"));

                    var stopped = pending.Where(pair => string.Equals(pair.Observed, WorkerStopped, StringComparison.Ordinal)).ToArray();
                    if (stopped.Length > 0)
                    {
                        throw new InvalidOperationException(
                            $"The queue '{stopped[0].Row.Queue}' of the {workerName} reported '{WorkerStopped}' after {elapsed.Elapsed.TotalSeconds:F1} s; a queue whose worker stopped is never accepted, and '{stopped[0].Row.State}' was expected.");
                    }
                }
                else
                {
                    lastError = $"the {workerName} answered {(int)response.StatusCode} ({response.ReasonPhrase})";
                }
            }

            if (elapsed.Elapsed >= HealthyDeadline)
            {
                throw new TimeoutException(
                    $"The {workerName} did not report the queues its registered work implies, after {elapsed.Elapsed.TotalSeconds:F1} s (deadline {HealthyDeadline.TotalSeconds:F0} s): "
                    + lastReport + "."
                    + (lastError is null ? string.Empty : $" Last error: {lastError}."));
            }

            await Task.Delay(QueueReadInterval, cancellationToken);
        }
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
