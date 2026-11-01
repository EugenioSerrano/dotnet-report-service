using System.Globalization;
using DotNet.Testcontainers.Containers;
using Npgsql;
using ReportService.Testing;

namespace ReportService.IntegrationTests;

/// <summary>The pinned PostgreSQL image starts and answers a query. The class owns its container.</summary>
public sealed class PostgreSqlReachabilityTests : IAsyncLifetime
{
    private const int Port = 5432;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AttemptBound = TimeSpan.FromSeconds(5);

    private readonly PinnedImage _image = CompositionPins.Load().Database;
    private readonly string _password = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private IContainer? _container;

    public async ValueTask InitializeAsync()
    {
        _container = ContainerSmoke.Builder(_image, "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", _password)
            .WithPortBinding(Port, assignRandomHostPort: true)
            .Build();
        await ContainerSmoke.StartAsync(_container, _image, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task PinnedServerAcceptsAConnectionAndAnswersAQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = _container!.Hostname,
            Port = _container.GetMappedPublicPort(Port),
            Username = "postgres",
            Password = _password,
            Database = "postgres",
            // The container is given no certificate and no GSS credential, and this
            // connection only proves reachability: it asks for neither transport.
            SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString;

        // The server initialises and restarts once; the wait ends when it accepts
        // connections, and each attempt is bounded so one stalled connection attempt
        // cannot outlast the deadline.
        await Wait.UntilAsync(
            $"PostgreSQL ({_image.Reference})",
            async token =>
            {
                await using var probe = new NpgsqlConnection(connectionString);
                await probe.OpenAsync(token);
                return true;
            },
            Deadline,
            TimeSpan.FromSeconds(1),
            AttemptBound,
            cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        var answer = await command.ExecuteScalarAsync(cancellationToken);

        Assert.Equal(1, Convert.ToInt32(answer, CultureInfo.InvariantCulture));
        Assert.StartsWith(_image.Tag.Split('.')[0] + ".", connection.ServerVersion, StringComparison.Ordinal);
    }
}
