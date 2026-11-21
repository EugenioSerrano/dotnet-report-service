using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Api.Artifacts;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.AppHost;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// The restart and the exhausted budget over the whole local composition: the real render-worker
/// container, stopped with <c>docker kill</c> and started again, and the job read from the
/// composition's own database and storage root. One composition serves every case of the class,
/// because the stop/start cases share the expensive setup; each case drives its own job.
/// <para>
/// The storage this class opens is the root the AppHost declares (<c>SharedStorageRoot</c>) and
/// the three processes read, so an object written here is an object the service sees. A kill is
/// evidence only when the attempt had started: every case waits for the worker's own start line
/// (or for the part it commits) before it stops the container, and the published document is read
/// by the independent judge, downloaded through the API's verifying reader, and held to the page
/// count the publication recorded.
/// </para>
/// </summary>
public sealed class RestartAndExhaustionEndToEndTests : IClassFixture<RestartComposition>
{
    private const string Consumer = "consumer-a";

    private static readonly TimeSpan Terminal = TimeSpan.FromMinutes(8);

    private static readonly TimeSpan RenderStart = TimeSpan.FromMinutes(3);

    private readonly RestartComposition _composition;

    /// <summary>Uses the class's one composition.</summary>
    /// <param name="composition">The composition the class started.</param>
    public RestartAndExhaustionEndToEndTests(RestartComposition composition) => _composition = composition;

    /// <summary>
    /// A print interrupted before any part was committed finishes with one document: the
    /// container is killed once the render had started and no part existed, started again, and the
    /// job ends succeeded with one artifact row, one part commit, no other row naming an
    /// <c>outputs/</c> object, and the object verified by size and SHA-256. The demo's first arm,
    /// and its transcript: the restarted PDF is read by the judge, downloaded through the API's
    /// verifying reader, and its pages equal the count the publication recorded.
    /// </summary>
    [Fact]
    public async Task TheRestartMidPrintFinishesWithOneDocument()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evidence = new StringBuilder();
        var reportJobId = await SubmitAsync(cancellationToken);
        var outputId = await WaitForOutputIdAsync(reportJobId, cancellationToken);

        await Wait.UntilAsync(
            "the render of the first attempt to start before the container is stopped",
            async token => await StartedAsync(outputId, token) >= 1 && await PartCommitsAsync(outputId, token) == 0,
            RenderStart,
            TimeSpan.FromMilliseconds(100),
            cancellationToken);

        var killed = Stopwatch.GetTimestamp();
        await _composition.Worker.StopAsync(cancellationToken);
        evidence.AppendLine(CultureInfo.InvariantCulture, $"killed {Short(_composition.Worker.Id)} before any part commit");
        await _composition.Worker.StartAsync(cancellationToken);

        var job = await WaitForTerminalAsync(reportJobId, cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(killed);
        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Equal("SUCCEEDED", job.OutputStatus);
        Assert.NotNull(job.ArtifactId);
        Assert.Equal(1, await ArtifactsAsync(reportJobId, cancellationToken));
        Assert.Equal(1, await PartCommitsAsync(outputId, cancellationToken));
        Assert.Equal(1, await ArtifactsNamingAnOutputsObjectAsync(reportJobId, cancellationToken));
        Assert.True(job.PageCount is > 0, "the published artifact had to record its page count");

        var bytes = await DownloadAsync(reportJobId, job.ArtifactId!, cancellationToken);
        Assert.Equal(job.SizeBytes, bytes.LongLength);
        Assert.Equal(job.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        var verdict = await _composition.JudgeAsync("restart-mid-print", bytes, cancellationToken);
        Assert.True(verdict.Passed, "the restarted document failed the judge: " + verdict.Describe());
        Assert.Equal(job.PageCount, verdict.Pages);

        var orphans = await OrphansAsync(reportJobId, outputId, cancellationToken);
        evidence.AppendLine(CultureInfo.InvariantCulture,
            $"artifact {job.ArtifactId} sha256={job.Sha256} bytes={bytes.LongLength} pages={job.PageCount}; "
            + $"judge pages={verdict.Pages}; orphans={orphans}; kill->terminal={elapsed.TotalSeconds:F1}s");
        RunEnvironment.WriteEvidence("restart-mid-print.transcript.txt", Encoding.UTF8.GetBytes(evidence.ToString()));
        TestContext.Current.TestOutputHelper?.WriteLine(evidence.ToString());
    }

    /// <summary>
    /// A print interrupted after its part was committed prints nothing again: the container is
    /// killed once the part row exists, started again, and the resumed attempt returns its own
    /// commit - one part commit, one artifact, and the artifact's object verifies.
    /// </summary>
    [Fact]
    public async Task TheRestartAfterThePartCommitPrintsNothingAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await SubmitAsync(cancellationToken);
        var outputId = await WaitForOutputIdAsync(reportJobId, cancellationToken);

        await Wait.UntilAsync(
            "the first part to be committed before the container is stopped",
            async token => await PartCommitsAsync(outputId, token) == 1,
            TimeSpan.FromMinutes(4),
            TimeSpan.FromMilliseconds(100),
            cancellationToken);

        await _composition.Worker.StopAsync(cancellationToken);
        await _composition.Worker.StartAsync(cancellationToken);

        var job = await WaitForTerminalAsync(reportJobId, cancellationToken);
        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Equal(1, await PartCommitsAsync(outputId, cancellationToken));
        Assert.Equal(1, await ArtifactsAsync(reportJobId, cancellationToken));
        Assert.NotNull(job.ArtifactId);
        var bytes = await ReadArtifactAsync(job, cancellationToken);
        Assert.Equal(job.SizeBytes, bytes.LongLength);
        Assert.Equal(job.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        Assert.Equal(1, await ArtifactsNamingAnOutputsObjectAsync(reportJobId, cancellationToken));
    }

    /// <summary>
    /// Every allowed attempt is killed: three attempts are observed for the block, **none of them
    /// writes an artifact**, the output and the job end failed with the generic code and the none
    /// token, nothing is published, the consumer's sentence is the formatter's own, and every
    /// stored string and every log line of the worker that runs the failure path is free of a host
    /// name, a path, a stack frame, a storage key and an object key. The demo's second arm, and
    /// its transcript.
    /// </summary>
    [Fact]
    public async Task TheKillOnEveryAttemptEndsFailedWithoutArtifact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var evidence = new StringBuilder();
        var reportJobId = await SubmitAsync(cancellationToken);
        var outputId = await WaitForOutputIdAsync(reportJobId, cancellationToken);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var started = attempt;
            await Wait.UntilAsync(
                $"the render of attempt {attempt} to start before the container is stopped",
                async token => await StartedAsync(outputId, token) >= started,
                RenderStart,
                TimeSpan.FromMilliseconds(100),
                cancellationToken);

            // Nothing is published before the last attempt either: every attempt in flight is
            // asserted to have written no artifact.
            Assert.Equal(0, await ArtifactsAsync(reportJobId, cancellationToken));
            await _composition.Worker.StopAsync(cancellationToken);
            evidence.AppendLine(CultureInfo.InvariantCulture, $"attempt {attempt}: killed {Short(_composition.Worker.Id)}");
            await _composition.Worker.StartAsync(cancellationToken);
        }

        var job = await WaitForTerminalAsync(reportJobId, cancellationToken);
        Assert.Equal("FAILED", job.Status);
        Assert.Equal("EXECUTION_FAILED", job.ErrorCode);
        Assert.Equal("FAILED", job.OutputStatus);
        Assert.Equal("EXECUTION_FAILED", job.OutputErrorCode);
        Assert.Equal("exhausted cause=none", job.OutputErrorReason);
        Assert.Null(job.ArtifactId);
        Assert.Equal(0, await ArtifactsAsync(reportJobId, cancellationToken));
        Assert.Equal(3, await StartedAsync(outputId, cancellationToken));
        Assert.Equal(0, await ArtifactsNamingAnOutputsObjectAsync(reportJobId, cancellationToken));

        var sentence = ErrorMessages.Format(job.OutputErrorCode!, job.OutputErrorReason, NullLogger.Instance);
        Assert.False(string.IsNullOrWhiteSpace(sentence), "the consumer's sentence had to be the formatter's own");
        Assert.DoesNotContain(Environment.MachineName, sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outputs/", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", sentence, StringComparison.Ordinal);

        var scan = await _composition.ScanTheFailurePathAsync(job, cancellationToken);
        Assert.True(scan.Clean, "the failure path carried an internal detail: " + scan.Describe());

        evidence.AppendLine(CultureInfo.InvariantCulture, $"job {job.Status} code={job.ErrorCode} reason={job.OutputErrorReason}");
        evidence.AppendLine("consumer sentence: " + sentence);
        evidence.AppendLine(CultureInfo.InvariantCulture, $"planted-value scan: clean over {scan.Inspected} strings and lines");
        RunEnvironment.WriteEvidence("kill-on-every-attempt.transcript.txt", Encoding.UTF8.GetBytes(evidence.ToString()));
        TestContext.Current.TestOutputHelper?.WriteLine(evidence.ToString());
    }

    /// <summary>
    /// A restart after the last attempt changes nothing: the container is started again after the
    /// third failure, the job is still failed with the same code and reason, and no fourth attempt
    /// appears.
    /// </summary>
    [Fact]
    public async Task TheRestartAfterTheLastAttemptChangesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await SubmitAsync(cancellationToken);
        var outputId = await WaitForOutputIdAsync(reportJobId, cancellationToken);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var started = attempt;
            await Wait.UntilAsync(
                $"the render of attempt {attempt} to start",
                async token => await StartedAsync(outputId, token) >= started,
                RenderStart,
                TimeSpan.FromMilliseconds(100),
                cancellationToken);
            await _composition.Worker.StopAsync(cancellationToken);
            await _composition.Worker.StartAsync(cancellationToken);
        }

        var failed = await WaitForTerminalAsync(reportJobId, cancellationToken);
        Assert.Equal("FAILED", failed.Status);
        Assert.Equal("EXECUTION_FAILED", failed.OutputErrorCode);
        Assert.Equal("exhausted cause=none", failed.OutputErrorReason);
        await _composition.Worker.StopAsync(cancellationToken);
        await _composition.Worker.StartAsync(cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        var final = await ReadAsync(reportJobId, cancellationToken);
        Assert.NotNull(final);
        Assert.Equal(failed.Status, final!.Status);
        Assert.Equal(failed.ErrorCode, final.ErrorCode);
        Assert.Equal(failed.OutputErrorReason, final.OutputErrorReason);
        Assert.Equal(3, await StartedAsync(outputId, cancellationToken));
    }

    /// <summary>
    /// The composition's restart policy is the runtime's own reading of the argument the
    /// composition declared: <c>docker inspect</c> reports <c>always</c> for the render worker. A
    /// runtime that ignores the argument is a stop, not a finding to live with.
    /// </summary>
    [Fact]
    public async Task TheCompositionDeclaresTheRestartPolicyAlways()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var policy = await _composition.Worker.RestartPolicyAsync(cancellationToken);
        RunEnvironment.WriteEvidence(
            "render-worker-restart-policy.txt",
            Encoding.UTF8.GetBytes($"container {_composition.Worker.Id} restart policy: {policy}{Environment.NewLine}"));
        Assert.Equal("always", policy);
    }

    private static string Short(string containerId) => containerId[..Math.Min(12, containerId.Length)];

    // One accepted job over a ready upload of the pilot's own twenty-five records, printed from
    // the version the class's fixture took.
    private async Task<string> SubmitAsync(CancellationToken cancellationToken)
    {
        var uploadId = await _composition.ReadyUploadAsync(cancellationToken);
        using var api = _composition.App.CreateHttpClient("api");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                "{\"template\":{\"id\":\"" + RestartComposition.Template + "\",\"version\":\"" + RestartComposition.Version + "\"},"
                + "\"data\":{\"uploadId\":\"" + uploadId + "\",\"profile\":\"header-items-v1\"},\"outputs\":[\"pdf\"]}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"the job had to be accepted: {(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private async Task<string> WaitForOutputIdAsync(string reportJobId, CancellationToken cancellationToken)
    {
        string? outputId = null;
        await Wait.UntilAsync(
            $"the output of {reportJobId} to be recorded",
            async token =>
            {
                outputId = await _composition.ScalarAsync<string?>(
                    "SELECT o.output_id FROM report_service.report_outputs o WHERE o.report_job_id = @ReportJobId",
                    new { ReportJobId = reportJobId },
                    token);
                return outputId is not null;
            },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMilliseconds(100),
            cancellationToken);
        return outputId!;
    }

    // How many attempts of the block the worker recorded as started: the line the activity writes
    // when it begins is the evidence a kill cannot fake, because a killed attempt's line is
    // already in the container's log.
    private async Task<int> StartedAsync(string outputId, CancellationToken cancellationToken)
    {
        var logs = await _composition.Worker.LogsAsync(cancellationToken);
        var marker = "output " + outputId + " started, attempt ";
        return logs.Split('\n').Count(line => line.Contains(marker, StringComparison.Ordinal));
    }

    private Task<long> PartCommitsAsync(string outputId, CancellationToken cancellationToken) =>
        _composition.ScalarAsync<long>(
            "SELECT count(*) FROM report_service.pdf_part_commits WHERE output_id = @OutputId",
            new { OutputId = outputId },
            cancellationToken);

    private Task<long> ArtifactsAsync(string reportJobId, CancellationToken cancellationToken) =>
        _composition.ScalarAsync<long>(
            "SELECT count(*) FROM report_service.artifacts a JOIN report_service.report_outputs o ON o.output_id = a.output_id WHERE o.report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken);

    // The read side of D1/D9: only the one artifact may name an object under the output's own
    // namespace.
    private Task<long> ArtifactsNamingAnOutputsObjectAsync(string reportJobId, CancellationToken cancellationToken) =>
        _composition.ScalarAsync<long>(
            "SELECT count(*) FROM report_service.artifacts a JOIN report_service.report_outputs o ON o.output_id = a.output_id WHERE o.report_job_id = @ReportJobId AND a.object_key LIKE 'outputs/%'",
            new { ReportJobId = reportJobId },
            cancellationToken);

    // The objects a killed attempt left under the output's namespace, counted against the keys
    // the rows reference: they are orphans, never artifacts, and the MEM records the count.
    private async Task<int> OrphansAsync(string reportJobId, string outputId, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_composition.StorageRoot, "outputs", reportJobId, outputId);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = await _composition.QueryAsync<string>(
            """
            SELECT a.object_key FROM report_service.artifacts a WHERE a.output_id = @OutputId
            UNION
            SELECT c.object_key FROM report_service.pdf_part_commits c WHERE c.output_id = @OutputId
            """,
            new { OutputId = outputId },
            cancellationToken);
        foreach (var key in keys.Where(key => !string.IsNullOrEmpty(key)))
        {
            referenced.Add(Path.Combine(_composition.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar)));
        }

        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Count(file => !referenced.Contains(file));
    }

    private async Task<JobState> WaitForTerminalAsync(string reportJobId, CancellationToken cancellationToken)
    {
        JobState? job = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to end",
            async token =>
            {
                job = await ReadAsync(reportJobId, token);
                return job is not null
                       && (string.Equals(job.Status, "SUCCEEDED", StringComparison.Ordinal)
                           || string.Equals(job.Status, "FAILED", StringComparison.Ordinal));
            },
            Terminal,
            TimeSpan.FromMilliseconds(200),
            cancellationToken);
        return job!;
    }

    private Task<JobState?> ReadAsync(string reportJobId, CancellationToken cancellationToken) =>
        _composition.QuerySingleAsync<JobState>(
            """
            SELECT j.status AS "Status", j.error_code AS "ErrorCode", j.error_message AS "ErrorMessage",
                   o.output_id AS "OutputId", o.status AS "OutputStatus", o.error_code AS "OutputErrorCode",
                   o.error_reason AS "OutputErrorReason",
                   a.artifact_id AS "ArtifactId", a.object_key AS "ObjectKey", a.sha256 AS "Sha256",
                   a.size_bytes AS "SizeBytes", a.page_count AS "PageCount"
              FROM report_service.report_jobs j
              LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
              LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
             WHERE j.report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken);

    private async Task<byte[]> ReadArtifactAsync(JobState job, CancellationToken cancellationToken)
    {
        Assert.NotNull(job.ObjectKey);
        var read = await _composition.Storage.OpenReadAsync(
            ObjectKey.Create(job.ObjectKey!),
            job.SizeBytes!.Value,
            job.Sha256!,
            cancellationToken);
        Assert.True(read.Succeeded, "the published artifact had to verify through the storage port");
        using var buffer = new MemoryStream();
        await using var content = read.Value!;
        await content.Content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    // The artifact through the API's download: the production reader streams the stored object and
    // aborts on a mismatch, so a byte the publication recorded cannot reach this method unverified.
    private async Task<byte[]> DownloadAsync(string reportJobId, string artifactId, CancellationToken cancellationToken)
    {
        using var api = _composition.App.CreateHttpClient("api");
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                $"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}{ArtifactListEndpoint.RouteSuffix}/{artifactId}{ArtifactDownloadEndpoint.RouteSuffix}",
                UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var buffer = new MemoryStream();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        await content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}

/// <summary>One job as the composition's records show it after a terminal status.</summary>
public sealed class JobState
{
    public string Status { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public string? OutputId { get; set; }

    public string? OutputStatus { get; set; }

    public string? OutputErrorCode { get; set; }

    public string? OutputErrorReason { get; set; }

    public string? ArtifactId { get; set; }

    public string? ObjectKey { get; set; }

    public string? Sha256 { get; set; }

    public long? SizeBytes { get; set; }

    public int? PageCount { get; set; }
}

/// <summary>What a planted-value scan found: the strings and lines it inspected, and the leaks.</summary>
public sealed record FailureScan(int Inspected, IReadOnlyList<string> Leaks)
{
    /// <summary>Whether every inspected string and line was free of an internal detail.</summary>
    public bool Clean => Leaks.Count == 0;

    /// <summary>The leaks, for a failure that names them.</summary>
    public string Describe() => Leaks.Count == 0 ? "clean" : string.Join("; ", Leaks);
}

/// <summary>What the judge said about one document.</summary>
public sealed record Verdict(bool Passed, int Pages, IReadOnlyList<string> Failures)
{
    /// <summary>The verdict as a line, for a failure that names it.</summary>
    public string Describe() => Failures.Count == 0 ? "passed" : string.Join("; ", Failures);
}

/// <summary>
/// The render worker of the composition, by the id its model reports: the three operations the
/// cases need - the kill, the start and the policy reading - over the runtime's own CLI.
/// <para>
/// The extended harness <c>RenderWorkerContainer</c> lives in the integration suite's assembly
/// and builds a container it starts itself; the composition's container is the AppHost model's,
/// and reaching it from this suite would mean a test project referencing another test project and
/// dragging the fixture library into the composition's suite. The reason is recorded in the MEM,
/// and the harness's three members are exercised by the worker-level class against the image it
/// starts, so they are not dead.
/// </para>
/// </summary>
public sealed class CompositionWorker(string id)
{
    /// <summary>The runtime's identifier of the composition's render worker.</summary>
    public string Id { get; } = id;

    /// <summary>Stops the worker as a lost process stops: <c>docker kill</c> sends SIGKILL.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public Task StopAsync(CancellationToken cancellationToken) => DockerAsync(["kill", Id], cancellationToken);

    /// <summary>Starts the container again and waits for its health, which the policy may have already done.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await DockerAsync(["start", Id], cancellationToken);
        await Wait.UntilAsync(
            "the render worker to answer its health after the restart",
            async token =>
            {
                var state = (await DockerAsync(["inspect", "--format", "{{.State.Health.Status}}", Id], token)).Trim();
                return string.Equals(state, "healthy", StringComparison.Ordinal);
            },
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
    }

    /// <summary>The restart policy the runtime reports for the container.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<string> RestartPolicyAsync(CancellationToken cancellationToken) =>
        (await DockerAsync(["inspect", "--format", "{{.HostConfig.RestartPolicy.Name}}", Id], cancellationToken)).Trim();

    /// <summary>The container's own log.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public Task<string> LogsAsync(CancellationToken cancellationToken) => DockerAsync(["logs", Id], cancellationToken);

    private static async Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, $"docker {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}

/// <summary>
/// The one composition every case of the class drives: the AppHost's own, with the API, both
/// workers and the real render-worker container, against the class's temporary consumer registry.
/// The taken version is seeded through the composition's own storage and metadata database, so the
/// worker never reads a template source; the storage this class opens is the composition's
/// declared root, and the worker the cases stop is the container the model reports.
/// </summary>
public sealed class RestartComposition : IAsyncLifetime
{
    /// <summary>The template the class's jobs print.</summary>
    public const string Template = "pilot-simple";

    /// <summary>The version the class's jobs print.</summary>
    public const string Version = "1.0.0";

    private const string Consumer = "consumer-a";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(8);

    private static readonly string OracleImage = "report-service-pdf-oracle:0.2.0-dev";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-restart",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    private NpgsqlDataSource? _dataSource;

    /// <summary>The running composition.</summary>
    public DistributedApplication App { get; private set; } = null!;

    /// <summary>The composition's storage root, through its own provider.</summary>
    public IObjectStore Storage { get; private set; } = null!;

    /// <summary>The composition's storage root, as the host processes see it.</summary>
    public string StorageRoot { get; private set; } = null!;

    /// <summary>The composition's render worker, by the container id its model reports.</summary>
    public CompositionWorker Worker { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(
            registry,
            """{ "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }""",
            cancellationToken);

        var builder = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");
        App = await builder.BuildAsync(cancellationToken);
        await App.StartAsync(cancellationToken);
        using var api = App.CreateHttpClient("api");
        await Wait.UntilAsync(
            "the API answering /health",
            async token =>
            {
                using var response = await api.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            HealthyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        var connectionString = await App.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        _dataSource = NpgsqlDataSource.Create(connectionString);

        // The root the AppHost declares and the three processes read: the same directory, never a
        // class's own, so every object this suite writes is one the service can read.
        StorageRoot = Path.Combine(RepositoryPaths.Root, SharedStorageRoot.DirectoryName);
        Directory.CreateDirectory(StorageRoot);
        Storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot, CreateIfMissing = true },
            StorageRoot,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        Worker = await AttachWorkerAsync(cancellationToken);
        await TakenAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        await App.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    /// <summary>
    /// The planted-value scan of the failure contract over the failure path: every stored string of the job
    /// and its output, and every line the worker that runs the failure path logged, held to no
    /// host name, path, stack frame, storage key or object key.
    /// </summary>
    /// <param name="job">The job read after its terminal status.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<FailureScan> ScanTheFailurePathAsync(JobState job, CancellationToken cancellationToken)
    {
        var stored = await QuerySingleAsync<StoredStrings>(
            """
            SELECT j.status AS "Status", j.error_code AS "Code", j.error_reason AS "Reason", j.error_message AS "Message",
                   o.status AS "OutputStatus", o.error_code AS "OutputCode", o.error_reason AS "OutputReason"
              FROM report_service.report_jobs j
              LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
             WHERE j.report_job_id = (SELECT report_job_id FROM report_service.report_outputs WHERE output_id = @OutputId)
            """,
            new { OutputId = job.OutputId! },
            cancellationToken);

        var values = new List<string?>
        {
            stored?.Status, stored?.Code, stored?.Reason, stored?.Message,
            stored?.OutputStatus, stored?.OutputCode, stored?.OutputReason,
            job.Status, job.ErrorCode, job.ErrorMessage, job.OutputStatus, job.OutputErrorCode, job.OutputErrorReason,
        };
        values.AddRange((await Worker.LogsAsync(cancellationToken)).Split('\n'));

        var leaks = new List<string>();
        var inspected = 0;
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            inspected++;
            foreach (var marker in Markers())
            {
                if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    leaks.Add(marker);
                    break;
                }
            }
        }

        return new FailureScan(inspected, leaks.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Runs one scalar statement over the composition's database.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Its parameters.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<T> ScalarAsync<T>(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException("the statement answered no value");
    }

    /// <summary>Runs one row statement over the composition's database.</summary>
    /// <typeparam name="T">The row's type.</typeparam>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Its parameters.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<T?> QuerySingleAsync<T>(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<T>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    /// <summary>Runs one column statement over the composition's database.</summary>
    /// <typeparam name="T">The column's type.</typeparam>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Its parameters.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))];
    }

    /// <summary>Creates a ready upload of the pilot's own twenty-five records.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<string> ReadyUploadAsync(CancellationToken cancellationToken)
    {
        var uploads = new PostgresUploadStore(_dataSource!);
        var snapshot = Encoding.UTF8.GetBytes(
            await File.ReadAllTextAsync(
                Path.Combine(RepositoryPaths.Root, "samples", "templates", Template, "sample.json"),
                cancellationToken));
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                Consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                now,
                now.AddHours(1)),
            cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId,
            UploadContentOutcome.Complete,
            snapshot.LongLength,
            snapshot.LongLength,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(snapshot)),
            now,
            cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(1), cancellationToken));

        var key = ObjectKey.FromSegments("snapshots", uploadId, "a");
        using var content = new MemoryStream(snapshot, writable: false);
        var written = await Storage.WriteAsync(key, content, cancellationToken);
        Assert.True(written.Succeeded);
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    key,
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    written.Value!.Sha256,
                    snapshot.LongLength,
                    written.Value.Sha256,
                    written.Value.Size,
                    now),
                now,
                cancellationToken));
        return uploadId;
    }

    /// <summary>
    /// The independent judge over one published document: the oracle's image reads the bytes, the
    /// snapshot they were printed from and the package's expectation, and answers whether the
    /// pages and every record and character hold.
    /// </summary>
    /// <param name="name">The name the verdict is kept under.</param>
    /// <param name="pdf">The artifact's bytes.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<Verdict> JudgeAsync(string name, byte[] pdf, CancellationToken cancellationToken)
    {
        var input = Path.Combine(_directory, "judged", name);
        Directory.CreateDirectory(input);
        await File.WriteAllBytesAsync(Path.Combine(input, name + ".pdf"), pdf, cancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(input, name + ".snapshot.json"),
            await File.ReadAllBytesAsync(
                Path.Combine(RepositoryPaths.Root, "samples", "templates", Template, "sample.json"),
                cancellationToken),
            cancellationToken);

        var mountInput = ToDockerPath(input);
        var expectations = ToDockerPath(Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "OracleExpectations"));
        await DockerAsync(
            [
                "run", "--rm",
                "-v", mountInput + ":/in",
                "-v", expectations + ":/expectations",
                OracleImage,
                "verdict", "--pdf", $"/in/{name}.pdf",
                "--fixture", $"/in/{name}.snapshot.json",
                "--expectation", "/expectations/" + Template + ".json",
                "--out", $"/in/{name}.verdict.json",
                "--reading", $"/in/{name}.reading.json",
            ],
            cancellationToken);

        using var verdict = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(input, name + ".verdict.json"), cancellationToken));
        using var reading = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(input, name + ".reading.json"), cancellationToken));
        var failures = verdict.RootElement.TryGetProperty("failures", out var list)
            ? list.EnumerateArray()
                .Select(failure => string.Join(
                    " | ",
                    new[] { "rule", "class", "field", "detail" }
                        .Select(field => failure.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                        .Where(part => !string.IsNullOrEmpty(part))))
                .ToList()
            : [];
        var pages = reading.RootElement.TryGetProperty("pages", out var pagesElement) && pagesElement.ValueKind == JsonValueKind.Array
            ? pagesElement.GetArrayLength()
            : 0;
        RunEnvironment.WriteEvidence(
            name + ".verdict.json",
            await File.ReadAllBytesAsync(Path.Combine(input, name + ".verdict.json"), cancellationToken));
        return new Verdict(verdict.RootElement.GetProperty("passed").GetBoolean(), pages, failures);
    }

    private async Task<CompositionWorker> AttachWorkerAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var worker = await App.ResourceNotifications.WaitForResourceAsync(
            TestComposition.RenderWorkerName,
            resource => resource.Snapshot.Properties.Any(property =>
                string.Equals(property.Name, "container.id", StringComparison.Ordinal) && property.Value is not null),
            deadline.Token);
        var id = Convert.ToString(
            worker.Snapshot.Properties.Single(property => string.Equals(property.Name, "container.id", StringComparison.Ordinal)).Value,
            CultureInfo.InvariantCulture)!;
        return new CompositionWorker(id);
    }

    // The version the class's jobs print, taken in the test process with the product's own take:
    // the record and the bytes land where the composition sees them, and the worker never reads
    // a template source.
    private async Task TakenAsync(CancellationToken cancellationToken)
    {
        var versions = new PostgresTemplateVersionStore(_dataSource!);
        var source = TemplateSourceClass.Over(Storage);
        await source.RemoveAsync(Template, Version);
        await source.PublishAsync(Template, Version, PackageFiles());
        var options = new TemplateSourceOptions();
        var take = new TemplateTake(
            new StorageTemplateSource(Storage, options, new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance),
            versions,
            Storage,
            new TemplateManifestReader(),
            new TemplateVersionConflict(NullLogger<TemplateVersionConflict>.Instance),
            [],
            new TemplateTakeMetrics(),
            TimeProvider.System,
            Options.Create(options),
            NullLogger<TemplateTake>.Instance);
        var outcome = await take.TakeAsync(Template, Version, Consumer, reportJobId: null, cancellationToken);
        Assert.NotNull(outcome.Record);
        Assert.Equal(Version, outcome.Record!.Version);
    }

    private static Dictionary<string, byte[]> PackageFiles()
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "templates", Template);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            files[relative] = File.ReadAllBytes(file);
        }

        return files;
    }

    // The markers a failure path must never carry: the host's name, a path, a stack frame, a
    // storage key's root and an object key's root.
    private string[] Markers() =>
    [
        Environment.MachineName,
        StorageRoot,
        RepositoryPaths.Root,
        " at ",
        "   at ",
        "outputs/",
        "snapshots/",
        "System.",
        "Exception",
        "StackTrace",
    ];

    private static string ToDockerPath(string path) =>
        OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    private static async Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, $"docker {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}

/// <summary>Every stored string of one job and its output.</summary>
public sealed class StoredStrings
{
    public string? Status { get; set; }

    public string? Code { get; set; }

    public string? Reason { get; set; }

    public string? Message { get; set; }

    public string? OutputStatus { get; set; }

    public string? OutputCode { get; set; }

    public string? OutputReason { get; set; }
}
