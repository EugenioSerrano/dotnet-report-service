using Npgsql;
using ReportService.IntegrationTests.Shared;

namespace ReportService.IntegrationTests;

/// <summary>
/// A per-class handle onto the run-level shared PostgreSQL server
/// (<see cref="SharedPostgres"/>): the class keeps its per-test isolation — every case
/// still asks for a database of its own, owned by the application role — but the server
/// itself is started once per run and reused, not started per class. The databases this
/// class created are dropped from the shared server at the class's teardown, so the
/// server does not accumulate databases across the run.
/// <para>
/// This handle exposes only the per-test-safe surface (<see cref="CreateDatabaseAsync"/>,
/// <see cref="Application"/>, <see cref="OpenAsync"/>). A class that must pause/unpause the
/// server or read server-level catalogue state uses <see cref="DedicatedPostgresFixture"/>
/// instead, which owns a container of its own.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly List<string> _databases = [];
    private readonly Lock _gate = new();
    private SharedPostgres _server = null!;

    /// <summary>The application role the init script creates.</summary>
    public const string ApplicationRole = SharedPostgres.ApplicationRole;

    private SharedPostgres Server =>
        _server ?? throw new InvalidOperationException(
            "The shared PostgreSQL server is not available. The assembly fixture "
            + nameof(SharedPostgres) + " must be registered and initialised before any test class runs.");

    public ValueTask InitializeAsync()
    {
        _server = SharedPostgres.Current
            ?? throw new InvalidOperationException(
                "The shared PostgreSQL server has not been initialised. The assembly fixture "
                + nameof(SharedPostgres) + " must run before the test classes.");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        string[] databases;
        lock (_gate)
        {
            databases = [.. _databases];
            _databases.Clear();
        }

        foreach (var database in databases)
        {
            await Server.DropDatabaseAsync(database, CancellationToken.None);
        }
    }

    /// <summary>The connection string of the application role on <paramref name="database"/>.</summary>
    public string Application(string database) => Server.Application(database);

    /// <summary>Creates a database of its own on the shared server, owned by the application role, and returns its name.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = await Server.CreateDatabaseAsync(cancellationToken);
        lock (_gate)
        {
            _databases.Add(name);
        }

        return name;
    }

    /// <summary>Opens a connection as the application role on <paramref name="database"/>.</summary>
    public Task<NpgsqlConnection> OpenAsync(string database, CancellationToken cancellationToken) =>
        Server.OpenAsync(database, cancellationToken);
}
