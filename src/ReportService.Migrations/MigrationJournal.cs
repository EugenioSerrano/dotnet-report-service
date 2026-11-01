using System.Diagnostics.CodeAnalysis;
using Dapper;
using Npgsql;

namespace ReportService.Migrations;

/// <summary>
/// The journal table that records which script applied and with which checksum.
/// It lives in <c>public</c>, which the database owner owns, so the application
/// role can create it before the first script runs.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Runs only against a real PostgreSQL server; covered by the integration suite.")]
public static class MigrationJournal
{
    /// <summary>The journal's qualified name.</summary>
    public const string TableName = "public.schema_migrations";

    private const string CreateStatement = """
        CREATE TABLE IF NOT EXISTS public.schema_migrations (
            script_name  text        PRIMARY KEY,
            checksum     text        NOT NULL,
            applied_at   timestamptz NOT NULL DEFAULT now()
        );
        """;

    private const string ReadStatement =
        "SELECT script_name AS \"ScriptName\", checksum AS \"Checksum\" FROM public.schema_migrations ORDER BY script_name";

    private const string InsertStatement =
        "INSERT INTO public.schema_migrations (script_name, checksum) VALUES (@name, @checksum)";

    /// <summary>Creates the journal when it does not exist yet.</summary>
    public static async Task EnsureAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await connection.ExecuteAsync(new CommandDefinition(CreateStatement, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Reads every row, in name order.</summary>
    public static async Task<IReadOnlyList<JournalEntry>> ReadAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var rows = await connection.QueryAsync<JournalRow>(new CommandDefinition(ReadStatement, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(row => new JournalEntry(row.ScriptName, row.Checksum))];
    }

    /// <summary>Records one applied script; it runs inside the script's own transaction.</summary>
    public static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(script);
        await connection.ExecuteAsync(new CommandDefinition(
            InsertStatement,
            new { name = script.Name, checksum = script.Checksum },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed class JournalRow
    {
        public string? ScriptName { get; set; }

        public string? Checksum { get; set; }
    }
}
