using System.Diagnostics.CodeAnalysis;
using Dapper;
using Npgsql;
using ReportService.Storage;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplatePackages.Records;

/// <summary>
/// The taken template versions in the application's own database, read and
/// written with constant SQL over a pooled data source. The insert lets the
/// primary key decide a race: the loser reads the winner's row in the same
/// transaction and reports it, so two takes of one version can never leave two
/// rows. The stored manifest is read back as written, so no reader needs the
/// source format.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Runs only against a real PostgreSQL server; covered by the integration suite.")]
public sealed class PostgresTemplateVersionStore : ITemplateVersionStore
{
    /// <summary>How long one command of this store may take.</summary>
    public const int CommandTimeoutSeconds = 5;

    private const string SelectColumns = """
        SELECT template_id       AS "TemplateId",
               version           AS "Version",
               owner_consumer_id AS "OwnerConsumerId",
               shared            AS "Shared",
               manifest_version  AS "ManifestVersion",
               engine            AS "EngineText",
               engine_version    AS "EngineVersion",
               schema_ref        AS "SchemaRef",
               commit_sha        AS "CommitSha",
               source_tree_sha   AS "SourceTreeSha",
               package_sha256    AS "PackageSha256",
               package_size      AS "PackageSize",
               file_count        AS "FileCount",
               object_key        AS "ObjectKey",
               manifest::text    AS "ManifestJson",
               taken_at          AS "TakenAt"
        FROM report_service.template_versions
        """;

    private const string FindSql = SelectColumns + """

        WHERE template_id = @TemplateId AND version = @Version
        """;

    private const string InsertSql = """
        INSERT INTO report_service.template_versions
            (template_id, version, owner_consumer_id, shared, manifest_version, engine, engine_version,
             schema_ref, commit_sha, source_tree_sha, package_sha256, package_size, file_count,
             object_key, manifest, taken_at)
        VALUES
            (@TemplateId, @Version, @OwnerConsumerId, @Shared, @ManifestVersion, @EngineText, @EngineVersion,
             @SchemaRef, @CommitSha, @SourceTreeSha, @PackageSha256, @PackageSize, @FileCount,
             @ObjectKey, CAST(@ManifestJson AS jsonb), @TakenAt)
        ON CONFLICT (template_id, version) DO NOTHING
        RETURNING template_id
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Reads and writes through <paramref name="dataSource"/>, which the process owns.</summary>
    /// <param name="dataSource">The pooled data source of the application's database.</param>
    public PostgresTemplateVersionStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<TemplateVersionRow>(new CommandDefinition(
                FindSql,
                new { TemplateId = templateId, Version = version },
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return row is null ? null : ToRecord(row);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new TemplateStoreUnavailableException("the taken version could not be read", exception);
        }
    }

    /// <inheritdoc />
    public async Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var parameters = new
        {
            record.TemplateId,
            record.Version,
            record.OwnerConsumerId,
            record.Shared,
            record.ManifestVersion,
            EngineText = TemplateEngineNames.ToStoredText(record.Engine),
            record.EngineVersion,
            record.SchemaRef,
            record.CommitSha,
            record.SourceTreeSha,
            record.PackageSha256,
            record.PackageSize,
            record.FileCount,
            ObjectKey = record.ObjectKey.ToString(),
            record.ManifestJson,
            TakenAt = record.TakenAt.ToUniversalTime().UtcDateTime,
        };

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var inserted = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                InsertSql,
                parameters,
                transaction,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (inserted is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return TemplateVersionInsert.Inserted(record);
            }

            var existing = await connection.QuerySingleAsync<TemplateVersionRow>(new CommandDefinition(
                FindSql,
                new { record.TemplateId, record.Version },
                transaction,
                commandTimeout: CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return TemplateVersionInsert.Existing(ToRecord(existing));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new TemplateStoreUnavailableException("the taken version could not be recorded", exception);
        }
    }

    private static TemplateVersionRecord ToRecord(TemplateVersionRow row) =>
        new(
            row.TemplateId,
            row.Version,
            row.OwnerConsumerId,
            row.Shared,
            row.ManifestVersion,
            TemplateEngineNames.TryParseStoredText(row.EngineText, out var engine)
                ? engine.Value
                : throw new InvalidOperationException("the row holds an engine this build does not know"),
            row.EngineVersion,
            row.SchemaRef,
            row.CommitSha,
            row.SourceTreeSha,
            row.PackageSha256,
            row.PackageSize,
            row.FileCount,
            ObjectKey.Create(row.ObjectKey),
            TemplateManifestJson.ReadTyped(row.ManifestJson),
            row.ManifestJson,
            new DateTimeOffset(DateTime.SpecifyKind(row.TakenAt, DateTimeKind.Utc)));

    /// <summary>One row of the table, as the statements above read it.</summary>
    internal sealed class TemplateVersionRow
    {
        /// <summary>The template's identifier.</summary>
        public string TemplateId { get; init; } = string.Empty;

        /// <summary>The version.</summary>
        public string Version { get; init; } = string.Empty;

        /// <summary>The owning consumer.</summary>
        public string OwnerConsumerId { get; init; } = string.Empty;

        /// <summary>Whether every consumer may use it.</summary>
        public bool Shared { get; init; }

        /// <summary>The manifest format.</summary>
        public string ManifestVersion { get; init; } = string.Empty;

        /// <summary>The engine, in the table's own vocabulary.</summary>
        public string EngineText { get; init; } = string.Empty;

        /// <summary>The engine version.</summary>
        public string EngineVersion { get; init; } = string.Empty;

        /// <summary>The package-relative path of the schema.</summary>
        public string SchemaRef { get; init; } = string.Empty;

        /// <summary>The commit the version was read at.</summary>
        public string CommitSha { get; init; } = string.Empty;

        /// <summary>The git tree of the version's folder at that commit.</summary>
        public string SourceTreeSha { get; init; } = string.Empty;

        /// <summary>The package's SHA-256.</summary>
        public string PackageSha256 { get; init; } = string.Empty;

        /// <summary>The package's size.</summary>
        public long PackageSize { get; init; }

        /// <summary>How many files the package holds.</summary>
        public int FileCount { get; init; }

        /// <summary>The content-addressed key of the package.</summary>
        public string ObjectKey { get; init; } = string.Empty;

        /// <summary>The stored manifest, canonical JSON.</summary>
        public string ManifestJson { get; init; } = string.Empty;

        /// <summary>When the version was taken.</summary>
        public DateTime TakenAt { get; init; }
    }
}
