using System.Diagnostics.CodeAnalysis;
using Dapper;
using Npgsql;

namespace ReportService.ServiceDefaults;

/// <summary>Reads the migration scripts a database reports as applied.</summary>
public interface ISchemaJournalReader
{
    /// <summary>The applied script names in name order; an empty list while the journal does not exist.</summary>
    Task<IReadOnlyList<string>> ReadAppliedScriptsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the journal from the real database with Dapper, with a bounded
/// connect and command time so a database that does not answer is reported
/// rather than waited on.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Runs only against a real PostgreSQL server; covered by the integration suite.")]
public sealed class NpgsqlSchemaJournalReader : ISchemaJournalReader
{
    /// <summary>How long a connection attempt and a command may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private const string JournalQuery = "SELECT script_name FROM public.schema_migrations ORDER BY script_name";

    private readonly string _connectionString;

    /// <summary>Reads with <paramref name="connectionString"/>, which is never written anywhere.</summary>
    public NpgsqlSchemaJournalReader(string connectionString) => _connectionString = connectionString;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ReadAppliedScriptsAsync(CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Timeout = (int)Timeout.TotalSeconds,
            CommandTimeout = (int)Timeout.TotalSeconds,
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));

        try
        {
            return [.. await connection.QueryAsync<string>(new CommandDefinition(JournalQuery, cancellationToken: cancellationToken))];
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }
}
