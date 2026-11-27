using System.Globalization;
using Npgsql;
using ReportService.Testing;
using ReportService.Testing.TemplateChecks;

namespace ReportService.EndToEndTests;

/// <summary>
/// The known-bad accounting control over the whole local composition: a package the take
/// admits — its amount field names a text field, so every admission rule is satisfied —
/// but whose block can never be accounted, because the marking reads a record's amount
/// from a field that holds text. The real Chromium worker's render fails with
/// <c>PAGINATION_FAILED</c>, the PDF output fails, no artifact row and no published object
/// exist for it, and the job's other requested output (HTML) is unaffected. The failure
/// is deterministic, so it is never retried; its diagnostics carry the reason and the
/// record ordinal, never a value.
/// <para>
/// The tamper reaches the worker through the taken package itself, never through
/// production code: the worker carries no tamper seam between composition and
/// verification, and one was not added (the scope-lock keeps the product untouched for a
/// test). The seam-based control — an amount changed in the served document after the
/// reference is extracted — is the rendering level's, where the host owns that seam
/// (<c>ReportService.RenderingHost</c>'s <c>AC-AMOUNT</c>); this case proves the published
/// outcome the acceptance criterion names.
/// </para>
/// </summary>
public sealed class PageAccountingKnownBadEndToEndTests : IDisposable
{
    private const int Records = 60;

    private static readonly TimeSpan SettledDeadline = TimeSpan.FromMinutes(8);

    private readonly MultiBlockStage _stage = new(
        accounted: true,
        templateId: "page-accounting-bad",
        amountField: "description");

    /// <inheritdoc />
    public void Dispose() => _stage.Dispose();

    [Fact]
    public async Task ABlockTheAccountingCannotReadFailsThePdfOutputAndPublishesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await _stage.StartAsync(cancellationToken);
        using var checks = new PageAccountingTakeChecks(TimeProvider.System, new InMemorySampleRenderer().AlwaysRendered());
        await _stage.TakeTemplateAsync(composition, cancellationToken, checks.Checks);
        var uploadId = await _stage.SeedConfirmingUploadAsync(composition, cancellationToken, recordsInSnapshot: Records);
        await _stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

        var reportJobId = await _stage.SubmitAsync(composition, uploadId, cancellationToken, outputs: "\"pdf\",\"html\"");
        var job = await WaitForSettledAsync(composition.DataSource, reportJobId, cancellationToken);

        // Some outputs succeeded and one failed: the distinct partially-succeeded terminal
        // state, with no job-level error (the per-output isolation the baseline has).
        Assert.Equal("PARTIALLY_SUCCEEDED", job.Status);
        Assert.Null(job.ErrorCode);

        var pdf = Assert.Single(job.Outputs, output => output.Format == "PDF");
        Assert.Equal("FAILED", pdf.Status);
        Assert.Equal("PAGINATION_FAILED", pdf.ErrorCode);
        Assert.Null(pdf.ArtifactId);
        Assert.Null(pdf.MediaType);

        var html = Assert.Single(job.Outputs, output => output.Format == "HTML");
        Assert.Equal("SUCCEEDED", html.Status);
        Assert.NotNull(html.ArtifactId);
        Assert.Equal("text/html; charset=utf-8", html.MediaType);

        // No published PDF: the only artifact of the job is the HTML one.
        Assert.DoesNotContain(job.Outputs, output => output.Format == "PDF" && output.ArtifactId is not null);

        RunEnvironment.WriteEvidence(
            "accounted-known-bad.evidence.txt",
            System.Text.Encoding.UTF8.GetBytes(
                $"pdf={pdf.Status}/{pdf.ErrorCode} artifact={pdf.ArtifactId ?? "none"}{Environment.NewLine}"
                + $"html={html.Status} artifact={html.ArtifactId}{Environment.NewLine}"));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"known bad: pdf {pdf.Status}/{pdf.ErrorCode}, html {html.Status}");
    }

    private static async Task<SettledJob> WaitForSettledAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        SettledJob? job = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to settle",
            async token =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT j.status, j.error_code,
                           o.format, o.status, o.error_code, a.artifact_id, a.media_type
                      FROM report_service.report_jobs j
                      LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
                      LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
                     WHERE j.report_job_id = @id
                    """;
                command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
                await using var reader = await command.ExecuteReaderAsync(token);
                var status = string.Empty;
                string? jobError = null;
                var outputs = new List<SettledOutput>();
                while (await reader.ReadAsync(token))
                {
                    status = reader.GetString(0);
                    jobError = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!reader.IsDBNull(2))
                    {
                        outputs.Add(new SettledOutput(
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5),
                            reader.IsDBNull(6) ? null : reader.GetString(6)));
                    }
                }

                job = new SettledJob(status, jobError, outputs);
                return status is "SUCCEEDED" or "FAILED" or "CANCELLED" or "PARTIALLY_SUCCEEDED";
            },
            SettledDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return job!;
    }

    private sealed record SettledJob(string Status, string? ErrorCode, IReadOnlyList<SettledOutput> Outputs);

    private sealed record SettledOutput(string Format, string Status, string? ErrorCode, string? ArtifactId, string? MediaType);
}
