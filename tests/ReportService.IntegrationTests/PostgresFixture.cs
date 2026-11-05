using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using ReportService.Testing;

namespace ReportService.IntegrationTests;

/// <summary>
/// One PostgreSQL container for a test class, initialised with the repository's
/// own application-role script, so the tests run as the same non-superuser role
/// the composition uses. The fixture owns the container and removes it; every
/// case asks for a database of its own.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const int ContainerPort = 5432;

    private readonly PinnedImage _image = CompositionPins.Load().Database;
    private readonly string _bootstrapPassword = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _rolePassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private IContainer? _container;
    private int _databases;

    /// <summary>The application role the init script creates.</summary>
    public const string ApplicationRole = "reportservice";

    /// <summary>The password of the application role, generated for this container.</summary>
    public string RolePassword => _rolePassword;

    /// <summary>The password of the server's bootstrap superuser, generated for this container.</summary>
    public string BootstrapPassword => _bootstrapPassword;

    /// <summary>The repository's init script.</summary>
    public static string InitScriptPath =>
        Path.Combine(RepositoryPaths.Root, "src", "ReportService.AppHost", "postgres-init", "10-application-role.sh");

    /// <summary>The connection string of the server's bootstrap user, for creating a database and reading the catalog.</summary>
    public string Bootstrap(string database = "postgres") =>
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
        var cancellationToken = TestContext.Current.CancellationToken;
        _container = ContainerSmoke.Builder(_image, "postgres-metadata")
            .WithEnvironment("POSTGRES_PASSWORD", _bootstrapPassword)
            .WithEnvironment("REPORTSERVICE_DB_PASSWORD", _rolePassword)
            .WithResourceMapping(new FileInfo(InitScriptPath), "/docker-entrypoint-initdb.d/")
            .WithPortBinding(ContainerPort, assignRandomHostPort: true)
            .Build();
        try
        {
            await ContainerSmoke.StartAsync(
                _container,
                _image,
                token => ReportService.Testing.Wait.UntilAsync(
                    $"PostgreSQL ({_image.Reference}) with the application role",
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
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates a database of its own, owned by the application role, and returns its name.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = "metadata_it_" + Interlocked.Increment(ref _databases).ToString(CultureInfo.InvariantCulture);
        await using var connection = new NpgsqlConnection(Bootstrap());
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition($"CREATE DATABASE \"{name}\" OWNER {ApplicationRole}", cancellationToken: cancellationToken));
        return name;
    }

    /// <summary>Opens a connection as the application role on <paramref name="database"/>.</summary>
    public async Task<NpgsqlConnection> OpenAsync(string database, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(Application(database));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Stops answering without losing the container, so a client sees an unreachable server.</summary>
    public Task PauseAsync(CancellationToken cancellationToken) => _container!.PauseAsync(cancellationToken);

    /// <summary>Answers again, with the same container and its data.</summary>
    public Task UnpauseAsync(CancellationToken cancellationToken) => _container!.UnpauseAsync(cancellationToken);
}
