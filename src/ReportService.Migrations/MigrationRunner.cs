using System.Diagnostics.CodeAnalysis;
using Dapper;
using Npgsql;
using ReportService.ServiceDefaults;

namespace ReportService.Migrations;

/// <summary>
/// One run of the migration set: one session, one advisory lock, the journal
/// created if absent, then each script to apply in its own transaction together
/// with its journal row. A refusal or a failure leaves the schema as it was.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Runs only against a real PostgreSQL server; covered by the integration suite.")]
public static class MigrationRunner
{
    /// <summary>
    /// The advisory lock of a run: the eight ASCII bytes <c>ReportSv</c> read as
    /// a big-endian 64-bit integer. It serialises two migrators on one database
    /// and is released when the session ends.
    /// </summary>
    public const long AdvisoryLockKey = 5937275307721773942;

    private static readonly string LockStatement = $"SELECT pg_advisory_lock({AdvisoryLockKey})";

    private static readonly string UnlockStatement = $"SELECT pg_advisory_unlock({AdvisoryLockKey})";

    /// <summary>Applies <paramref name="scripts"/> to the database at <paramref name="connectionString"/>.</summary>
    public static async Task<MigrationResult> RunAsync(
        string connectionString,
        IReadOnlyList<MigrationScript> scripts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(LockStatement, cancellationToken: cancellationToken)).ConfigureAwait(false);
            try
            {
                return await ApplyAsync(connection, scripts, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // The lock is released when the session ends anyway, so a session that
                // broke while unlocking must not replace the result of the run.
                await ReleaseAsync(connection).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new MigrationResult(
                [],
                [],
                SchemaVersion.None,
                [],
                new MigrationFailure(null, 0, SqlStateOf(exception), exception.GetType().Name, string.Empty));
        }
    }

    private static async Task<MigrationResult> ApplyAsync(
        NpgsqlConnection connection,
        IReadOnlyList<MigrationScript> scripts,
        CancellationToken cancellationToken)
    {
        await MigrationJournal.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
        var journal = await MigrationJournal.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
        var plan = MigrationPlanner.Plan(scripts, journal);
        var alreadyApplied = plan.AlreadyApplied.Select(script => script.Name).ToList();
        if (plan.Refusals.Count > 0)
        {
            return new MigrationResult([], alreadyApplied, CurrentVersion(alreadyApplied), plan.Refusals, null);
        }

        var applied = new List<string>();
        foreach (var script in plan.ToApply)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    script.Sql,
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                await MigrationJournal.InsertAsync(connection, transaction, script, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                applied.Add(script.Name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new MigrationResult(
                    applied,
                    alreadyApplied,
                    CurrentVersion(alreadyApplied),
                    [],
                    new MigrationFailure(
                        script.Name,
                        exception is PostgresException postgres ? postgres.Position : 0,
                        SqlStateOf(exception),
                        exception.GetType().Name,
                        MessageOf(exception)));
            }
        }

        return new MigrationResult(applied, alreadyApplied, CurrentVersion([.. alreadyApplied, .. applied]), [], null);
    }

    private static async Task ReleaseAsync(NpgsqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(UnlockStatement, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // The session ends with this run; ending it releases the lock.
        }
    }

    private static string CurrentVersion(IReadOnlyList<string> applied)
    {
        var highest = applied.Order(StringComparer.Ordinal).LastOrDefault();
        return highest ?? SchemaVersion.None;
    }

    private static string? SqlStateOf(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException)?.SqlState;

    /// <summary>The server's own message; anything else carries a host or a user, so it is never written.</summary>
    private static string MessageOf(Exception exception) =>
        exception is PostgresException postgres ? postgres.MessageText : "the statement was refused";
}
