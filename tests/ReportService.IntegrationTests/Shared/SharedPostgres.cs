using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using ReportService.Testing;

namespace ReportService.IntegrationTests.Shared;

/// <summary>
/// The run-level PostgreSQL server: one container started once per test assembly and
/// reused by every class that only needs its own database (the shareable classes,
/// through <see cref="PostgresFixture"/>). The container is initialised from the
/// repository's own application-role script and runs the same non-superuser role the
/// composition uses, so a per-test database created here is identical to the one a
/// per-class container used to create. It is removed at the end of the run by its own
/// handle (ADR-039 §3.4 item 5).
/// <para>
/// The accessor is deliberately per-test-safe only: it creates and drops databases of
/// the application role and opens connections on them. It does <b>not</b> expose any
/// container-wide operation (pause/unpause) or server-level catalogue read, because
/// those are visible to every other test on the shared server; the classes that need
/// them keep a dedicated container (<see cref="DedicatedPostgresFixture"/>).
/// </para>
/// </summary>
public sealed class SharedPostgres : IAsyncLifetime
{
    private const int ContainerPort = 5432;

    // Headroom for the parallel peak: up to the runner's maxParallelThreads classes run
    // at once, each with its own database and connection pool against this one server.
    // The default max_connections (100) is too tight for that peak; set it explicitly so
    // the connection-budget defect class (DISC-004 F5) is not reintroduced.
    private const int MaxConnections = 400;

    private readonly PinnedImage _image = CompositionPins.Load().Database;
    private readonly string _bootstrapPassword = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _rolePassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private IContainer? _container;
    private int _databases;

    /// <summary>The application role the init script creates.</summary>
    public const string ApplicationRole = "reportservice";

    /// <summary>
    /// The run-level server, available to the per-class <see cref="PostgresFixture"/>
    /// handles. Set once by the assembly fixture's initialisation, before any test class
    /// is constructed, and cleared when the run ends.
    /// </summary>
    public static SharedPostgres? Current { get; private set; }

    /// <summary>The repository's init script.</summary>
    public static string InitScriptPath =>
        Path.Combine(RepositoryPaths.Root, "src", "ReportService.AppHost", "postgres-init", "10-application-role.sh");

    private string Bootstrap(string database = "postgres") =>
        new NpgsqlConnectionStringBuilder
        {
            Host = _container!.Hostname,
            Port = _container.GetMappedPublicPort(ContainerPort),
            Username = "postgres",
            Password = _bootstrapPassword,
            Database = database,
        }.ConnectionString;

    /// <summary>The connection string of the application role on <paramref name="database"/>.</summary>
    public string Application(string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = _container!.Hostname,
            Port = _container.GetMappedPublicPort(ContainerPort),
            Username = ApplicationRole,
            Password = _rolePassword,
            Database = database,
        }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current?.CancellationToken ?? CancellationToken.None;
        _container = ContainerSmoke.Builder(_image, "postgres-shared")
            .WithEnvironment("POSTGRES_PASSWORD", _bootstrapPassword)
            .WithEnvironment("REPORTSERVICE_DB_PASSWORD", _rolePassword)
            .WithResourceMapping(new FileInfo(InitScriptPath), "/docker-entrypoint-initdb.d/")
            .WithPortBinding(ContainerPort, assignRandomHostPort: true)
            // The entrypoint runs the init scripts, then execs the server with this command,
            // so the application role is created exactly as before and the peak connection
            // budget is raised to cover the parallel classes.
            .WithCommand("postgres", "-c", "max_connections=" + MaxConnections.ToString(CultureInfo.InvariantCulture))
            .Build();
        try
        {
            await ContainerSmoke.StartAsync(
                _container,
                _image,
                token => ReportService.Testing.Wait.UntilAsync(
                    $"the shared PostgreSQL ({_image.Reference}) with the application role",
                    async probeToken =>
                    {
                        await using var connection = new NpgsqlConnection(Bootstrap());
                        await connection.OpenAsync(probeToken);
                        return await connection.ExecuteScalarAsync<long>(
                            new CommandDefinition("SELECT COUNT(*) FROM pg_roles WHERE rolname = @role", new { role = ApplicationRole }, cancellationToken: probeToken)) == 1;
                    },
                    TimeSpan.FromMinutes(2),
                    TimeSpan.FromSeconds(1),
                    token),
                cancellationToken);
        }
        catch
        {
            _container = null;
            throw;
        }

        Current = this;
    }

    public async ValueTask DisposeAsync()
    {
        Current = null;
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates a database of its own on the shared server, owned by the application role,
    /// and returns its name. The name carries a run-wide counter so two classes never
    /// collide on the shared server.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = "metadata_it_" + Interlocked.Increment(ref _databases).ToString(CultureInfo.InvariantCulture);
        await using var connection = new NpgsqlConnection(Bootstrap());
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition($"CREATE DATABASE \"{name}\" OWNER {ApplicationRole}", cancellationToken: cancellationToken));
        return name;
    }

    /// <summary>
    /// Drops <paramref name="database"/> from the shared server at the owning class's
    /// teardown, so the server does not accumulate databases across the run. The drop is
    /// forced, terminating any backend a disposed pool left behind, and a database that is
    /// already gone is not an error.
    /// </summary>
    public async Task DropDatabaseAsync(string database, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Bootstrap());
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", cancellationToken: cancellationToken));
    }

    /// <summary>Opens a connection as the application role on <paramref name="database"/>.</summary>
    public async Task<NpgsqlConnection> OpenAsync(string database, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(Application(database));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
