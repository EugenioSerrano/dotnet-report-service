using System.Text;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.Migrations.Tests;

/// <summary>
/// The scripts embedded in the assembly: the shipped set in name order, each
/// checksum equal to its file in the source tree, and the validation a bad name,
/// a number used twice or an empty script is refused by.
/// </summary>
public sealed class MigrationCatalogTests
{
    private const string Baseline = "0001_baseline.sql";

    private const string Uploads = "0002_uploads.sql";

    private const string Content = "0003_upload-content-snapshots-and-intents.sql";

    private const string Deadline = "0004_upload-verification-deadline.sql";

    private const string Jobs = "0005_report-jobs.sql";

    private const string PdfOutput = "0006_pdf-output.sql";

    private const string TemplateVersions = "0007_template-versions.sql";

    private const string JobProgress = "0008_job-progress.sql";

    private const string TemplateResolution = "0009_template-resolution.sql";

    private const string VersionSyntax = "0010_template-versions-version-syntax.sql";

    private const string Provenance = "0011_template-version-provenance.sql";

    private const string Locale = "0012_report-job-locale.sql";

    private const string Expiry = "0013_artifact-expiry.sql";

    private const string OutputReason = "0014_report-output-failure-reason.sql";

    private const string Presentation = "0015_presentation-provenance.sql";

    private const string RowBound = "0016_pdf-row-bound.sql";
    private const string JobIdempotency = "0017_job-idempotency.sql";
    private const string AttemptCauses = "0018_pdf-attempt-causes.sql";
    private const string PartiallySucceeded = "0019_job-partially-succeeded-status.sql";
    private const string CancelJobIntent = "0020_intent-kind-cancel-job.sql";
    private const string JobPriority = "0021_report-job-priority.sql";

    [Fact]
    public void TheShippedSetIsTheBaselineAndTheUploadsTableInNameOrder()
    {
        Assert.Equal(new[] { Baseline, Uploads, Content, Deadline, Jobs, PdfOutput, TemplateVersions, JobProgress, TemplateResolution, VersionSyntax, Provenance, Locale, Expiry, OutputReason, Presentation, RowBound, JobIdempotency, AttemptCauses, PartiallySucceeded, CancelJobIntent, JobPriority }, MigrationCatalog.Scripts.Select(script => script.Name));
        Assert.All(MigrationCatalog.Scripts, script => Assert.True(MigrationScriptName.IsValid(script.Name)));
        Assert.Equal(JobPriority, MigrationCatalog.ExpectedVersion);
    }

    /// <summary>
    /// The sixteenth script states whether a PDF output was refused by the row bound, beside
    /// the partition it belongs to: two nullable text columns - the code, checked against the
    /// one code this version writes, and the reason it carries - and the pair check that admits
    /// them only together. The output's own failure reason is not this script's:
    /// <c>report_outputs.error_reason</c> arrives with the fourteenth. It adds no table, drops
    /// nothing, creates no index and rewrites no other column. That the server applies it, and
    /// refuses a code without its reason, is a case against a real database, never a reading of
    /// this text.
    /// </summary>
    [Fact]
    public void TheRowBoundScriptAddsTheRefusalPairToThePartitionsAndNothingElse()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == RowBound);

        Assert.Contains("ALTER TABLE report_service.partitions", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN pdf_refusal_code   text NULL CHECK (pdf_refusal_code = 'ROW_BOUND_EXCEEDED')", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN pdf_refusal_reason text NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT partitions_pdf_refusal_pair CHECK ((pdf_refusal_code IS NULL) = (pdf_refusal_reason IS NULL))", script.Sql, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(script.Sql, "ADD COLUMN"));
        Assert.Equal(1, CountOf(script.Sql, "ADD CONSTRAINT"));
        Assert.DoesNotContain("report_outputs", script.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE INDEX", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", script.Sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The seventeenth script states the identity of a job's request and the key a consumer
    /// may bind to it: two nullable columns with their form and pair checks, the partial
    /// unique index that decides one job per consumer and key, and the guard function the
    /// job table already carries, replaced so the one trigger refuses a change of the four
    /// members that identify the request. It adds no table, drops nothing, backfills nothing
    /// and rewrites no row. That the server applies it, and refuses what it says it refuses,
    /// is a case against a real database, never a reading of this text.
    /// </summary>
    [Fact]
    public void TheJobIdempotencyScriptAddsTheKeyAndTheIdentityAndReplacesTheGuard()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == JobIdempotency);

        Assert.Contains("ALTER TABLE report_service.report_jobs", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN idempotency_key text NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (idempotency_key ~ '^[!-~]{1,255}$')", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN fingerprint     text NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (fingerprint ~ '^v[1-9][0-9]{0,2}:[0-9a-f]{64}$')", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT report_jobs_key_needs_fingerprint", script.Sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (idempotency_key IS NULL OR fingerprint IS NOT NULL)", script.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX report_jobs_idempotency", script.Sql, StringComparison.Ordinal);
        Assert.Contains("(consumer_id, idempotency_key)", script.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE idempotency_key IS NOT NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE FUNCTION report_service.report_jobs_locale_guard()", script.Sql, StringComparison.Ordinal);

        // The four members that identify the request are the guard's, on the one trigger the
        // table already carries: no second trigger and no second function is created.
        Assert.Contains("NEW.consumer_id     IS DISTINCT FROM OLD.consumer_id", script.Sql, StringComparison.Ordinal);
        Assert.Contains("NEW.upload_id       IS DISTINCT FROM OLD.upload_id", script.Sql, StringComparison.Ordinal);
        Assert.Contains("NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key", script.Sql, StringComparison.Ordinal);
        Assert.Contains("NEW.fingerprint     IS DISTINCT FROM OLD.fingerprint", script.Sql, StringComparison.Ordinal);
        Assert.Equal(0, CountOf(script.Sql, "CREATE TRIGGER"));
        Assert.Equal(2, CountOf(script.Sql, "ADD COLUMN"));
        Assert.Equal(1, CountOf(script.Sql, "CREATE UNIQUE INDEX"));
        Assert.DoesNotContain("CREATE TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", script.Sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The fifteenth script states what produced each printed block's presentation: one
    /// nullable <c>jsonb</c> column, the backfill every row written before it carries, and
    /// the completeness check that admits exactly two shapes - the complete object and that
    /// one value. It adds no table, drops nothing, creates no index and rewrites no other
    /// column; that the server applies it, and refuses a partial object and a JSON null, is a
    /// case against a real database, never a reading of this text.
    /// </summary>
    [Fact]
    public void ThePresentationScriptAddsOneColumnItsBackfillAndTheCompletenessCheckAndNothingElse()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == Presentation);

        Assert.Contains("ALTER TABLE report_service.pdf_part_commits", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN presentation jsonb NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("UPDATE report_service.pdf_part_commits", script.Sql, StringComparison.Ordinal);
        Assert.Contains("SET presentation = '{\"source\":\"unrecorded\"}'::jsonb", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN presentation SET NOT NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN presentation SET DEFAULT '{\"source\":\"unrecorded\"}'::jsonb", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT pdf_part_commits_presentation_complete CHECK (", script.Sql, StringComparison.Ordinal);
        Assert.Contains("presentation ?& ARRAY['culture', 'timeZone', 'clock', 'engine',", script.Sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_typeof(presentation -> 'icuVersion')    = 'string'", script.Sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_typeof(presentation -> 'tzDataVersion') = 'string'", script.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE INDEX", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script.Sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The twelfth script states the presentation a job is accepted with: two nullable
    /// columns with their length checks, and the guard that refuses a change of a value
    /// already set. It adds no table, drops nothing, backfills nothing and rewrites no
    /// row. That the server applies it, and refuses what it says it refuses, is a case
    /// against a real database, never a reading of this text.
    /// </summary>
    [Fact]
    public void TheLocaleScriptAddsTwoColumnsAndTheSetOnceGuardAndNothingElse()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == Locale);

        Assert.Contains("ALTER TABLE report_service.report_jobs", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN culture   text NULL CHECK (char_length(culture) BETWEEN 1 AND 64)", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN time_zone text NULL CHECK (char_length(time_zone) BETWEEN 1 AND 64)", script.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE FUNCTION report_service.report_jobs_locale_guard()", script.Sql, StringComparison.Ordinal);
        Assert.Contains("the job culture is set once", script.Sql, StringComparison.Ordinal);
        Assert.Contains("the job time zone is set once", script.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER report_jobs_locale_guard", script.Sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE ON report_service.report_jobs", script.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE report_service", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NOT NULL DEFAULT", script.Sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tenth script states the version syntax the product writes: one
    /// <c>ALTER TABLE</c> that adds the named check, and no table, no column and
    /// no drop of its own. That the server refuses a version outside the syntax is
    /// a case against a real database, never a reading of this text.
    /// </summary>
    [Fact]
    public void TheVersionSyntaxScriptAddsTheNamedCheckAndNothingElse()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == VersionSyntax);

        Assert.Equal(21, MigrationCatalog.Scripts.Count);
        Assert.Contains("ALTER TABLE report_service.template_versions", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT template_versions_version_syntax CHECK (", script.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ADD COLUMN", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", script.Sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The eleventh script states the publication provenance a version and a pin
    /// carry: it relaxes the two Git columns, adds the five provenance columns,
    /// names the four constraints, replaces the pin's completeness constraint and
    /// the guard function, and rewrites no row — no table or column is dropped, no
    /// value is updated, deleted or inserted. That the server applies it is a case
    /// against a real database, never a reading of this text.
    /// </summary>
    [Fact]
    public void TheProvenanceScriptRelaxesTwoColumnsAddsFiveAndReplacesOneConstraint()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == Provenance);

        Assert.Equal(21, MigrationCatalog.Scripts.Count);
        Assert.Equal(JobPriority, MigrationCatalog.Scripts[^1].Name);
        Assert.Equal(JobPriority, MigrationCatalog.ExpectedVersion);
        Assert.Contains("ALTER TABLE report_service.template_versions", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN commit_sha DROP NOT NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN source_tree_sha DROP NOT NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN source_descriptor_sha256 text NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN source_descriptor_key text NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN source_objects jsonb NULL", script.Sql, StringComparison.Ordinal);
        Assert.Equal(5, CountOf(script.Sql, "ADD COLUMN"));
        Assert.Contains("ADD CONSTRAINT template_versions_source_descriptor_sha256 CHECK (", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT template_versions_provenance CHECK (", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT template_references_source_descriptor_sha256 CHECK (", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT template_references_pin_shapes CHECK (", script.Sql, StringComparison.Ordinal);
        Assert.Equal(4, CountOf(script.Sql, "ADD CONSTRAINT"));
        Assert.Contains("DROP CONSTRAINT template_references_pin_complete", script.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE FUNCTION report_service.template_references_guard", script.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", script.Sql, StringComparison.OrdinalIgnoreCase);

        // Three DROP tokens and no more: the two NOT NULL marks and the superseded constraint.
        Assert.Equal(3, CountOf(script.Sql, "DROP"));
    }

    [Fact]
    public void EveryShippedScriptMatchesItsFileInTheSourceTree()
    {
        foreach (var script in MigrationCatalog.Scripts)
        {
            var path = Path.Combine(RepositoryPaths.Root, "src", "ReportService.Migrations", "Migrations", script.Name);
            var bytes = File.ReadAllBytes(path);

            Assert.Equal(MigrationScript.ChecksumOf(bytes), script.Checksum);
            Assert.Equal(Encoding.UTF8.GetString(bytes), script.Sql);
        }
    }

    [Fact]
    public void TheBaselineCreatesTheSchemaAndTheUploadsScriptAddsTheSessionsTable()
    {
        var baseline = Assert.Single(MigrationCatalog.Scripts, script => script.Name == Baseline);
        var uploads = Assert.Single(MigrationCatalog.Scripts, script => script.Name == Uploads);

        Assert.DoesNotContain("CREATE TABLE", baseline.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS report_service;", baseline.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE report_service.uploads", uploads.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJobsScriptCreatesTheJobTablesAndWidensTheIntentKinds()
    {
        var jobs = Assert.Single(MigrationCatalog.Scripts, script => script.Name == Jobs);

        Assert.Contains("CREATE TABLE report_service.report_jobs", jobs.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE report_service.report_outputs", jobs.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE report_service.template_references", jobs.Sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE report_service.intents", jobs.Sql, StringComparison.Ordinal);
        Assert.Contains("DROP CONSTRAINT intents_kind_check", jobs.Sql, StringComparison.Ordinal);
        Assert.Contains("'start-job'", jobs.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChecksumIsTheLowercaseHexSha256OfAKnownByteString()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            MigrationScript.ChecksumOf("abc"u8));
    }

    [Fact]
    public void ScriptsAreOrderedByTheirName()
    {
        var scripts = MigrationCatalog.Create(
        [
            (Name: "0010_ten.sql", Bytes: "SELECT 10"u8.ToArray()),
            (Name: "0002_two.sql", Bytes: "SELECT 2"u8.ToArray()),
        ]);

        Assert.Equal(["0002_two.sql", "0010_ten.sql"], scripts.Select(script => script.Name));
        Assert.Equal("0010_ten.sql", MigrationCatalog.ExpectedVersionOf(scripts));
    }

    [Fact]
    public void AMalformedNameIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MigrationCatalog.Create([(Name: "1_baseline.sql", Bytes: "SELECT 1"u8.ToArray())]));

        Assert.Contains("does not follow the name rule", error.Message, StringComparison.Ordinal);
        Assert.Contains("1_baseline.sql", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumberUsedTwiceIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MigrationCatalog.Create(
            [
                (Name: "0002_two.sql", Bytes: "SELECT 2"u8.ToArray()),
                (Name: "0002_other.sql", Bytes: "SELECT 3"u8.ToArray()),
            ]));

        Assert.Contains("migration script number 0002 is used twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyScriptIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MigrationCatalog.Create([(Name: "0001_baseline.sql", Bytes: []), (Name: "0002_blank.sql", Bytes: "  \n"u8.ToArray())]));

        Assert.Equal("migration script '0001_baseline.sql' is empty", error.Message);
    }

    [Fact]
    public void AnEmptySetIsCurrentAtNone()
    {
        var scripts = MigrationCatalog.Create([]);

        Assert.Empty(scripts);
        Assert.Equal(SchemaVersion.None, MigrationCatalog.ExpectedVersionOf(scripts));
    }

    /// <summary>
    /// The thirteenth script states when a published artifact stops being downloadable:
    /// one nullable column added, backfilled from `created_at`, then made NOT NULL with
    /// its check. It adds no table, drops nothing and creates no index; the backfill is
    /// what makes every row published before this change expired at once (D15). That the
    /// server applies it and refuses `expires_at &lt; created_at` is a case against a real
    /// database, never a reading of this text.
    /// </summary>
    [Fact]
    public void TheExpiryScriptAddsOneColumnBackfilledThenNotNullAndNothingElse()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == Expiry);

        Assert.Contains("ALTER TABLE report_service.artifacts ADD COLUMN expires_at timestamptz NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("UPDATE report_service.artifacts SET expires_at = created_at WHERE expires_at IS NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN expires_at SET NOT NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT artifacts_expiry_after_publication CHECK (expires_at >= created_at)", script.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX", script.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fourteenth script states why a PDF output failed, beside the reason the job
    /// already carries: one nullable text column added to the outputs table, with no
    /// default and no constraint of its own. It adds no table, drops nothing, backfills
    /// nothing, rewrites no row and builds no index. That the server applies it, and
    /// reports the column as nullable text, is a case against a real database, never a
    /// reading of this text.
    /// </summary>
    [Fact]
    public void TheOutputFailureReasonScriptAddsOneNullableColumnAndNothingElse()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == OutputReason);

        Assert.Equal(21, MigrationCatalog.Scripts.Count);
        Assert.Equal(JobPriority, MigrationCatalog.Scripts[^1].Name);
        Assert.Equal(1, CountOf(script.Sql, "ALTER TABLE"));
        Assert.Equal(1, CountOf(script.Sql, "ADD COLUMN"));
        Assert.Contains("ALTER TABLE report_service.report_outputs", script.Sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN error_reason text NULL", script.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NOT NULL", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DEFAULT", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ADD CONSTRAINT", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE INDEX", script.Sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The eighteenth script states the cause a rendering attempt recorded before it ended,
    /// keyed as the orchestration decision states it - output, block and attempt - and
    /// restricted to the two deadline classes of the guard and the watchdog. It creates one
    /// table with its primary key and its four checks, adds no column to a table that exists,
    /// backfills nothing and rewrites no row. The comment states how long the rows are kept.
    /// That the server applies it, and refuses a third cause and a repeated attempt, is a case
    /// against a real database, never a reading of this text.
    /// </summary>
    [Fact]
    public void TheAttemptCauseScriptCreatesTheKeyedTableAndNothingElse()
    {
        var script = Assert.Single(MigrationCatalog.Scripts, candidate => candidate.Name == AttemptCauses);

        Assert.Contains("CREATE TABLE report_service.pdf_attempt_causes (", script.Sql, StringComparison.Ordinal);
        Assert.Contains("output_id    text        NOT NULL REFERENCES report_service.report_outputs (output_id)", script.Sql, StringComparison.Ordinal);
        Assert.Contains("block_index  integer     NOT NULL CHECK (block_index >= 0)", script.Sql, StringComparison.Ordinal);
        Assert.Contains("attempt      integer     NOT NULL CHECK (attempt >= 1)", script.Sql, StringComparison.Ordinal);
        Assert.Contains("cause        text        NOT NULL CHECK (cause IN ('watchdog-deadline', 'guard-deadline'))", script.Sql, StringComparison.Ordinal);
        Assert.Contains("recorded_at  timestamptz NOT NULL", script.Sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (output_id, block_index, attempt)", script.Sql, StringComparison.Ordinal);

        // Kept with the job's own metadata: the retention cleanup of a later version removes
        // them with it, and the comment says so in the product's words.
        Assert.Contains("retention", script.Sql, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, CountOf(script.Sql, "CREATE TABLE"));
        Assert.DoesNotContain("ALTER TABLE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ADD COLUMN", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE INDEX", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", script.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", script.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
