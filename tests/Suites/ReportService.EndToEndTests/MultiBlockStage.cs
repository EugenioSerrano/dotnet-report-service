using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Data;
using ReportService.Data.Partitioning;
using ReportService.Jobs;
using ReportService.Pdf.Verification;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Templates;
using ReportService.Testing;
using ReportService.Testing.Fixtures;
using ReportService.Testing.MultiBlock;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;
using ReportService.Testing.Uploads;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// The multi-block stage over the whole local composition: a snapshot whose durable
/// partition is committed for an accepted job while its upload is still confirming,
/// the upload completed, and the job left to run the live path - the data step
/// partitions the snapshot into its ordered blocks, the real Chromium worker renders
/// them within the bounded window, the real merge worker assembles and stamps them, and
/// the published PDF is read back and checked page by page against the parts' own
/// commits. A job that leaves the partition to the live data step - the multi-block
/// PDF case - uses the same stage without the staged partition.
/// </summary>
internal sealed class MultiBlockStage : IDisposable
{
    /// <summary>The consumer every job and template belongs to.</summary>
    public const string Consumer = "consumer-a";

    /// <summary>The published template identity of the native multi-block package.</summary>
    public const string Template = "pilot-multi-block";

    /// <summary>The shipped sample the native package is built from.</summary>
    public const string Sample = "pilot-simple";

    /// <summary>The version every package declares.</summary>
    public const string Version = "1.0.0";

    private const int SnapshotRecords = 3_000;

    /// <summary>The accounted slice: it must reach the pilot dataset's one group larger than a block (G0012, 2 400 records).</summary>
    private const int AccountingSnapshotRecords = 6_000;
    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(15);

    private readonly bool _accounted;

    private readonly string? _templateId;

    private readonly string? _amountField;

    private readonly Func<int, (byte[] Snapshot, IReadOnlyList<AccountedRecord> Records)>? _snapshotFactory;

    private readonly Func<string, string, Dictionary<string, byte[]>>? _packageFactory;

    private readonly Func<Dictionary<string, byte[]>, Dictionary<string, byte[]>>? _packageTransform;

    private readonly IReadOnlyList<string> _compositionArguments;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-multi-block",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>The margins the sample declares, in millimetres.</summary>
    public static PageMargins Margins => PlacementConvention.FromMillimetres(new PageMarginsMm(15, 12, 18, 12));

    /// <summary>The reserved numbering area, in points: the same rectangle the injected contract declares.</summary>
    public static NumberingBounds Area { get; } = NumberingBounds.FromMillimetres(
        (double)NumberedMultiBlockFixture.AreaLeftMm,
        (double)NumberedMultiBlockFixture.AreaTopMm,
        (double)NumberedMultiBlockFixture.AreaWidthMm,
        (double)NumberedMultiBlockFixture.AreaHeightMm);

    /// <summary>The accounted fixture's numbering area, in points: the top band the accounting print leaves free.</summary>
    public static NumberingBounds AccountedArea { get; } = NumberingBounds.FromMillimetres(
        (double)PageAccountingFixtures.NumberingLeftMm,
        (double)PageAccountingFixtures.NumberingTopMm,
        (double)PageAccountingFixtures.NumberingWidthMm,
        (double)PageAccountingFixtures.NumberingHeightMm);

    /// <summary>The directory this stage's evidence and temporary files live under.</summary>
    public string Directory => _directory;

    /// <summary>The template identity the stage publishes: the native package or the accounted one.</summary>
    public string TemplateId => _accounted ? _templateId ?? PageAccountingFixtures.TemplateId : Template;

    /// <summary>The numbering area the stage's package declares, in points.</summary>
    public NumberingBounds ReserveArea => _accounted ? AccountedArea : Area;

    /// <summary>The records of the accounted snapshot, in ordinal order, for the independent oracle.</summary>
    public IReadOnlyList<AccountedRecord> AccountingRecords { get; private set; } = [];

    /// <summary>The declaration the accounted package and its expected values follow.</summary>
    public PageAccountingDeclaration AccountingDeclaration => PageAccountingDeclaration.Load();

    /// <summary>Creates the stage's directory.</summary>
    /// <param name="accounted">Whether the stage publishes and renders the page-accounting fixture instead of the native one.</param>
    /// <param name="templateId">The published identity, or the fixture's own.</param>
    /// <param name="amountField">The amount field the accounted fixture declares, or its own; a non-numeric field is the known-bad control.</param>
    /// <param name="snapshotFactory">The snapshot a case supplies instead of the stage's own, with its oracle.</param>
    /// <param name="packageFactory">The package a case supplies instead of the stage's own.</param>
    /// <param name="packageTransform">A change a case applies to the stage's own package.</param>
    /// <param name="compositionArguments">Settings a case gives the composition beside the stage's own.</param>
    public MultiBlockStage(
        bool accounted = false,
        string? templateId = null,
        string? amountField = null,
        Func<int, (byte[] Snapshot, IReadOnlyList<AccountedRecord> Records)>? snapshotFactory = null,
        Func<string, string, Dictionary<string, byte[]>>? packageFactory = null,
        Func<Dictionary<string, byte[]>, Dictionary<string, byte[]>>? packageTransform = null,
        IReadOnlyList<string>? compositionArguments = null)
    {
        _packageTransform = packageTransform;
        _compositionArguments = compositionArguments ?? [];
        _accounted = accounted;
        _templateId = templateId;
        _amountField = amountField;
        _snapshotFactory = snapshotFactory;
        _packageFactory = packageFactory;
        System.IO.Directory.CreateDirectory(_directory);
    }

    /// <summary>Starts the composition and returns the handles a case reads through.</summary>
    public async Task<Composition> StartAsync(CancellationToken cancellationToken)
    {
        // The composition's API serves the consumers its registry file names; every job of
        // this stage is submitted as consumer-a, so the file is written before the
        // composition is built and the model is given its path.
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(
            registry,
            """{ "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }""",
            cancellationToken);
        var builder = await TestComposition.CreateAsync(cancellationToken, [$"--Consumers:File={registry}", .. _compositionArguments]);
        var mergeWorkerContainerName = TestComposition.DeclaredMergeWorkerContainerName(builder);
        var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
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

        var connectionString = await app.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        return new Composition(app, dataSource, storage, new PostgresUploadStore(dataSource), new PostgresTemplateVersionStore(dataSource), mergeWorkerContainerName);
    }

    /// <summary>Takes the multi-block template with its numbering contract, font and native page counter.</summary>
    public async Task TakeTemplateAsync(
        Composition composition,
        CancellationToken cancellationToken,
        IReadOnlyList<ReportService.TemplateSource.Checks.ITemplateVersionCheck>? checks = null)
    {
        var source = TemplateSourceClass.Over(composition.Storage);
        await source.RemoveAsync(TemplateId, Version);
        await source.PublishAsync(TemplateId, Version, PackageFiles());

        var options = new TemplateSourceOptions();
        var take = new TemplateTake(
            new StorageTemplateSource(
                composition.Storage,
                options,
                new TemplateSourceMetrics(),
                NullLogger<StorageTemplateSource>.Instance),
            composition.Versions,
            composition.Storage,
            new TemplateManifestReader(),
            new TemplateVersionConflict(NullLogger<TemplateVersionConflict>.Instance),
            checks ?? [],
            new TemplateTakeMetrics(),
            TimeProvider.System,
            Options.Create(options),
            NullLogger<TemplateTake>.Instance);

        var outcome = await take.TakeAsync(TemplateId, Version, Consumer, reportJobId: null, cancellationToken);
        var record = outcome.Record
            ?? throw new InvalidOperationException("the multi-block template was not taken: " + outcome.Name);

        if (_accounted)
        {
            // The accounted fixture's declaration must survive the publication: a package the
            // renderer would refuse at take is refused here, in the fixture, not later.
            var totals = record.Manifest.Outputs?.Pdf?.PageTotals;
            if (totals is not { Enabled: true }
                || string.IsNullOrWhiteSpace(totals.AmountField)
                || totals.Footer?.Labels is not { BroughtForward: { Length: > 0 }, PageSubtotal: { Length: > 0 }, CarriedForward: { Length: > 0 } })
            {
                throw new InvalidOperationException("the accounted fixture's taken manifest carries no usable outputs.pdf.pageTotals");
            }

            var contract = record.Manifest.Outputs?.Pdf?.Numbering;
            if (contract?.Area is not { LeftMm: not null, TopMm: not null, WidthMm: not null, HeightMm: not null }
                || contract.Font is not { Family: { Length: > 0 }, File: { Length: > 0 }, SizePt: not null }
                || string.IsNullOrWhiteSpace(contract.Format)
                || string.IsNullOrWhiteSpace(contract.Culture))
            {
                throw new InvalidOperationException("the accounted fixture's taken outputs.pdf.numbering is incomplete");
            }

            return;
        }

        // The fixture's numbering contract must survive the injection: a silent no-op once
        // reached the covering run and the merge worker correctly refused the package
        // TEMPLATE_INVALID. The check reads the manifest the take recorded and the stored
        // JSON, so an injection that did not apply fails here, in the fixture, not later.
        var numbering = record.Manifest.Outputs?.Pdf?.Numbering
            ?? throw new InvalidOperationException(
                "the multi-block fixture's taken manifest carries no outputs.pdf.numbering; the YAML injection did not apply");
        var font = numbering.Font;
        if (numbering.Area is not { LeftMm: not null, TopMm: not null, WidthMm: not null, HeightMm: not null }
            || font is null
            || string.IsNullOrWhiteSpace(font.Family)
            || string.IsNullOrWhiteSpace(font.File)
            || font.SizePt is null
            || string.IsNullOrWhiteSpace(numbering.Format)
            || string.IsNullOrWhiteSpace(numbering.Culture))
        {
            throw new InvalidOperationException(
                "the multi-block fixture's taken outputs.pdf.numbering is incomplete");
        }

        using var stored = JsonDocument.Parse(record.ManifestJson);
        if (!stored.RootElement.TryGetProperty("outputs", out var outputs)
            || !outputs.TryGetProperty("pdf", out var pdf)
            || !pdf.TryGetProperty("numbering", out _))
        {
            throw new InvalidOperationException(
                "the multi-block fixture's stored manifest carries no outputs.pdf.numbering; the YAML injection did not apply");
        }
    }

    /// <summary>
    /// Creates the snapshot and confirms the upload, so the job is accepted on a
    /// confirming input. The composition's verifier may complete the upload while the
    /// case is still committing the partition - it verifies the snapshot this method
    /// wrote - so the handoff helpers accept either order instead of assuming the
    /// upload is still verifying.
    /// </summary>
    public async Task<string> SeedConfirmingUploadAsync(Composition composition, CancellationToken cancellationToken, int? recordsInSnapshot = null)
    {
        var recordsInSlice = recordsInSnapshot ?? (_accounted ? AccountingSnapshotRecords : SnapshotRecords);
        var (snapshot, records) = SnapshotForUpload(recordsInSlice);
        AccountingRecords = records;
        var seed = await ConfirmingUploadSeed.SeedAsync(
            composition.Uploads,
            composition.Storage,
            Consumer,
            snapshot,
            DateTimeOffset.UtcNow,
            cancellationToken);

        // The snapshot is kept beside the upload, so the partition is committed from the
        // same bytes the job will read.
        await File.WriteAllBytesAsync(Path.Combine(_directory, seed.UploadId + ".snapshot.json"), snapshot, cancellationToken);
        return seed.UploadId;
    }

    /// <summary>Submits the job for the upload and answers its identifier.</summary>
    public async Task<string> SubmitAsync(Composition composition, string uploadId, CancellationToken cancellationToken, string outputs = "\"pdf\"")
    {
        using var api = composition.App.CreateHttpClient("api");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                "{\"template\":{\"id\":\"" + TemplateId + "\",\"version\":\"" + Version + "\"},"
                + "\"data\":{\"uploadId\":\"" + uploadId + "\",\"profile\":\"header-items-v1\"},\"outputs\":[" + outputs + "]}",
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

    /// <summary>
    /// Commits the job's durable partition while its upload is still confirming: the
    /// delivered driver writes the blocks and the index page, and the partition record
    /// is written through the product's own store, so the live data step returns it.
    /// </summary>
    public async Task CommitPartitionAsync(Composition composition, string reportJobId, string uploadId, CancellationToken cancellationToken)
    {
        var snapshot = await File.ReadAllBytesAsync(Path.Combine(_directory, uploadId + ".snapshot.json"), cancellationToken);
        using var source = new MemoryStream(snapshot, writable: false);
        var driver = new DurablePartitionDriver(
            composition.Storage,
            new PartitionSpec(GroupKeyField: _accounted ? PageAccountingFixtures.GroupKey : null, "amount", PartitionLimits.Default));
        var partition = await driver.RunAsync(
            reportJobId,
            HeaderItemsReader.ReadAsync(source, cancellationToken),
            resumeFrom: null,
            cancellationToken);

        var indexBytes = BlockIndexPage.Serialize(partition.Index);
        var indexKey = await FindIndexKeyAsync(composition.Storage, reportJobId, cancellationToken);
        var record = new PartitionRecord(
            reportJobId,
            partition.Index.PartitionerVersion,
            partition.Index.Blocks.Count,
            partition.Index.Blocks.Sum(block => block.RecordCount),
            partition.Index.Blocks.Sum(block => block.RecordBytes),
            indexKey,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(indexBytes)),
            indexBytes.LongLength,
            DateTimeOffset.UtcNow);
        var jobs = new PostgresJobStore(composition.DataSource);
        var committed = await jobs.CommitPartitionAsync(record, cancellationToken);

        // The job may have committed the same partition from the same snapshot before
        // the fixture did, because the verifier can make the upload ready first. Either
        // write is the fixture's partition; the renderer reads that record from the
        // store, and the following assertions still require the same shape.
        Assert.True(
            committed.Outcome is PartitionCommitOutcome.Committed or PartitionCommitOutcome.AlreadyCommitted,
            committed.Outcome.ToString());
        Assert.Equal(record.BlockCount, committed.Existing.BlockCount);
        Assert.Equal(record.RecordCount, committed.Existing.RecordCount);
    }

    /// <summary>
    /// Completes the upload: the verifier may already have made it ready on its own -
    /// it verifies the snapshot the fixture wrote - so a READY upload is success and a
    /// VERIFYING one is committed here. Either way the upload must end READY: a
    /// rejected or expired upload still fails, and its status is named.
    /// </summary>
    public async Task CompleteUploadAsync(Composition composition, string uploadId, CancellationToken cancellationToken)
    {
        var snapshot = await File.ReadAllBytesAsync(
            Path.Combine(_directory, uploadId + ".snapshot.json"), cancellationToken);
        var seed = SeededUpload.FromSnapshot(uploadId, snapshot);
        var completed = await ConfirmingUploadSeed.CompleteAsync(
            composition.Uploads,
            composition.Storage,
            seed,
            Consumer,
            snapshot,
            DateTimeOffset.UtcNow,
            cancellationToken);

        if (completed.Status != UploadStatus.Ready)
        {
            throw new InvalidOperationException("the upload ended " + completed.Status + " instead of ready");
        }

        Assert.Equal(UploadStatus.Ready, completed.Status);
    }

    /// <summary>Creates the snapshot, uploads it and completes it in one act, so the job runs without a pre-committed partition.</summary>
    public async Task<string> SeedReadyUploadAsync(Composition composition, CancellationToken cancellationToken)
    {
        var uploadId = await SeedConfirmingUploadAsync(composition, cancellationToken);
        await CompleteUploadAsync(composition, uploadId, cancellationToken);
        return uploadId;
    }

    /// <summary>Reads the job's PDF output identifier, or <see langword="null"/> while no output is recorded.</summary>
    public async Task<string?> FindOutputIdAsync(
        Composition composition,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await composition.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT output_id FROM report_service.report_outputs WHERE report_job_id = @id AND format = 'PDF'";
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>Reads the output's part commits, ordered by block index.</summary>
    public async Task<IReadOnlyList<PartRow>> ReadPartsAsync(
        Composition composition,
        string outputId,
        CancellationToken cancellationToken)
    {
        var parts = new List<PartRow>();
        await using var connection = await composition.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT block_index, object_key, sha256, size_bytes, page_count
              FROM report_service.pdf_part_commits
             WHERE output_id = @id
             ORDER BY block_index
            """;
        command.Parameters.Add(new NpgsqlParameter("id", outputId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            parts.Add(new PartRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt32(4)));
        }

        return parts;
    }

    /// <summary>Reads one stored object to a file under the stage's directory and answers its path.</summary>
    public async Task<string> DownloadAsync(
        Composition composition,
        string key,
        long size,
        string sha,
        string name,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, name);
        var read = await composition.Storage.OpenReadAsync(ObjectKey.Create(key), size, sha, cancellationToken);
        Assert.True(read.Succeeded, read.Outcome.ToString());
        await using var content = read.Value!;
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.Content.CopyToAsync(file, cancellationToken);
        return path;
    }

    /// <summary>Waits for the job to end and answers its record with its one artifact.</summary>
    /// <param name="composition">The running composition.</param>
    /// <param name="reportJobId">The job to wait for.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <param name="deadline">How long the job may take, or the stage's own when null.</param>
    public async Task<EndedJob> WaitForEndAsync(
        Composition composition,
        string reportJobId,
        CancellationToken cancellationToken,
        TimeSpan? deadline = null)
    {
        EndedJob? ended = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to end",
            async token =>
            {
                ended = await ReadJobAsync(composition.DataSource, reportJobId, token);
                return ended is { Status: "SUCCEEDED" or "FAILED" or "CANCELLED" or "PARTIALLY_SUCCEEDED" };
            },
            deadline ?? JobDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return ended!;
    }

    /// <summary>Reads the job's committed partition record.</summary>
    /// <param name="composition">The running composition.</param>
    /// <param name="reportJobId">The job whose partition is read.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<PartitionRecord> ReadPartitionAsync(
        Composition composition,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        var jobs = new PostgresJobStore(composition.DataSource);
        return await jobs.FindPartitionAsync(reportJobId, cancellationToken)
            ?? throw new InvalidOperationException("the job has no committed partition");
    }

    /// <summary>Reads the committed index page through the storage port and parses it.</summary>
    /// <param name="composition">The running composition.</param>
    /// <param name="partition">The partition record whose index is read.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<BlockIndexPage> ReadIndexAsync(
        Composition composition,
        PartitionRecord partition,
        CancellationToken cancellationToken)
    {
        var read = await composition.Storage.OpenReadAsync(
            ObjectKey.Create(partition.IndexObjectKey),
            partition.IndexSizeBytes,
            partition.IndexSha256,
            cancellationToken);
        Assert.True(read.Succeeded, read.Outcome.ToString());
        await using var content = read.Value!;
        using var buffer = new MemoryStream();
        await content.Content.CopyToAsync(buffer, cancellationToken);
        return BlockIndexPage.Parse(buffer.ToArray());
    }

    /// <summary>
    /// The console lines a composition resource wrote. The resource is found in the
    /// application model by its model name, and its log is read under every name the
    /// orchestrator resolved for it: a resource the orchestrator runs is given a unique
    /// suffix (and one name per replica), and its log stream is kept under that resolved
    /// name, never under the bare model name. Each stream's backlog is read whole; when
    /// it is empty the stream is watched for a short window for lines still arriving. A
    /// resource the model does not hold, or - when lines are expected - a read that finds
    /// none, fails the case loudly: an empty read must never make a "does not contain"
    /// assertion pass.
    /// </summary>
    /// <param name="app">The running composition.</param>
    /// <param name="resourceName">The resource's model name.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <param name="expectLines">Whether the resource is known to have written lines by now.</param>
    public static async Task<IReadOnlyList<string>> ReadWorkerLogAsync(
        DistributedApplication app,
        string resourceName,
        CancellationToken cancellationToken,
        bool expectLines = true)
    {
        var resource = ResourceOf(app, resourceName);
        var loggers = app.Services.GetRequiredService<ResourceLoggerService>();
        var lines = new List<string>();

        // The resource's own overload resolves its orchestrator instances.
        await foreach (var batch in loggers.GetAllAsync(resource).WithCancellation(cancellationToken))
        {
            lines.AddRange(batch.Select(line => line.Content));
        }

        // The resolved names, as the orchestrator's own state events report them, each read
        // by name: the same streams, reached without relying on the overload alone.
        var names = await ResolvedNamesAsync(app, resource, cancellationToken);
        if (lines.Count == 0)
        {
            foreach (var name in names)
            {
                await foreach (var batch in loggers.GetAllAsync(name).WithCancellation(cancellationToken))
                {
                    lines.AddRange(batch.Select(line => line.Content));
                }
            }
        }

        if (lines.Count == 0)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await foreach (var batch in loggers.WatchAsync(resource).WithCancellation(stop.Token))
                {
                    lines.AddRange(batch.Select(line => line.Content));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The lines that arrived within the window are the evidence.
            }
        }

        if (expectLines && lines.Count == 0)
        {
            throw new InvalidOperationException(
                $"the log of '{resourceName}' read empty under its resolved names ({string.Join(", ", names)}), "
                + "though the case expects lines by now: the read cannot be evidence of anything");
        }

        return lines;
    }

    /// <summary>
    /// The names the orchestrator resolved for a resource - the model name with the unique
    /// suffix it gives every instance it runs - read from the resource's state events: the
    /// notification stream replays every resource's current state to a new subscriber, and
    /// each event carries the resolved identifier of the instance it is about. The stream
    /// is watched for a short window; the bare model name is included as well.
    /// </summary>
    /// <param name="app">The running composition.</param>
    /// <param name="resource">The resource.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>The names, model name first.</returns>
    public static async Task<IReadOnlyList<string>> ResolvedNamesAsync(
        DistributedApplication app,
        IResource resource,
        CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var names = new List<string> { resource.Name };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var update in notifications.WatchAsync(stop.Token).WithCancellation(stop.Token))
            {
                if (ReferenceEquals(update.Resource, resource) && !names.Contains(update.ResourceId, StringComparer.Ordinal))
                {
                    names.Add(update.ResourceId);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The window closed; the identifiers seen so far are the resolved names.
        }

        return names;
    }

    /// <summary>The application model's resource of a model name; fails when the model holds none.</summary>
    /// <param name="app">The running composition.</param>
    /// <param name="resourceName">The model name.</param>
    /// <returns>The resource.</returns>
    public static IResource ResourceOf(DistributedApplication app, string resourceName)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        return model.Resources.SingleOrDefault(resource => string.Equals(resource.Name, resourceName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"the composition has no resource named '{resourceName}' (it has: {string.Join(", ", model.Resources.Select(resource => resource.Name))})");
    }

    /// <summary>Reads one page's outside-area words, in painting order.</summary>
    public static IReadOnlyList<string> OutsideArea(PrintedPage page)
    {
        var area = Area;
        return [.. page.Words
            .Where(word => !Inside(word, area))
            .Select(word => word.Text)];
    }

    /// <summary>Whether a word's centre falls inside the reserved numbering area.</summary>
    public static bool Inside(PrintedWord word, NumberingBounds area)
    {
        var x = (word.Left + word.Right) / 2d;
        var y = (word.Top + word.Bottom) / 2d;
        return x >= area.LeftPt && x <= area.LeftPt + area.WidthPt
            && y >= area.TopPt && y <= area.TopPt + area.HeightPt;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(_directory))
            {
                System.IO.Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static async Task<string> FindIndexKeyAsync(IObjectStore storage, string reportJobId, CancellationToken cancellationToken)
    {
        await foreach (var info in storage.ListAsync($"index/{reportJobId}", cancellationToken))
        {
            return info.Key.ToString();
        }

        throw new InvalidOperationException("the durable index page was not written");
    }

    private static async Task<EndedJob> ReadJobAsync(NpgsqlDataSource dataSource, string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT j.status, j.error_code, o.output_id, o.status, o.error_code, o.error_reason,
                   a.artifact_id, a.object_key, a.sha256, a.size_bytes, a.page_count
              FROM report_service.report_jobs j
              LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
              LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
             WHERE j.report_job_id = @id
            """;
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new EndedJob("UNKNOWN", null, null, null, null, null, null, null, null, null);
        }

        return new EndedJob(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9));
    }

    private Dictionary<string, byte[]> PackageFiles() =>
        _packageTransform is null ? OwnPackageFiles() : _packageTransform(OwnPackageFiles());

    private Dictionary<string, byte[]> OwnPackageFiles()
    {
        if (_packageFactory is not null)
        {
            return _packageFactory(Consumer, TemplateId);
        }

        if (_accounted)
        {
            return MultiBlockFixtures.Package(Consumer, TemplateId, _amountField);
        }

        var root = System.IO.Path.Combine(RepositoryPaths.Root, "samples", "templates", Sample);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(root, file).Replace(System.IO.Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(file);
            if (string.Equals(relative, "template.yaml", StringComparison.Ordinal))
            {
                text = text.Replace("owner: dev-consumer", "owner: " + Consumer, StringComparison.Ordinal);
                text = text.Replace("id: " + Sample, "id: " + Template, StringComparison.Ordinal);
                text = NumberedMultiBlockFixture.InsertNumberingContract(text);
            }

            if (string.Equals(relative, "html/styles.css", StringComparison.Ordinal))
            {
                text = text.Replace(
                    "font-family: sans-serif;",
                    "font-family: \"Liberation Sans\", sans-serif;",
                    StringComparison.Ordinal);
                var newline = NumberedMultiBlockFixture.NewlineOf(text);
                text += newline
                    + "@page { @bottom-center { content: \"PART-COUNTER \" counter(page) \" of \" counter(pages); } @top-center { content: \"Statement\"; } }"
                    + newline;
            }

            files[relative] = Encoding.UTF8.GetBytes(text);
        }

        files["fonts/LiberationSans-Regular.ttf"] = File.ReadAllBytes(
            System.IO.Path.Combine(RepositoryPaths.Root, "tests", "Support", "ReportService.Testing", "Fonts", "LiberationSans-Regular.ttf"));
        return files;
    }

    private static byte[] Snapshot(int records)
    {
        var sample = JsonNode.Parse(File.ReadAllText(
            System.IO.Path.Combine(RepositoryPaths.Root, "samples", "templates", Sample, "sample.json")))!;
        var items = sample["items"]!.AsArray();
        var built = new JsonArray();
        for (var index = 0; index < records; index++)
        {
            var item = JsonNode.Parse(items[index % items.Count]!.ToJsonString())!;
            item["sequence"] = index + 1;
            built.Add(item);
        }

        sample["items"] = built;
        sample["header"]!["totals"]!["recordCount"] = records;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    /// <summary>The snapshot a job uploads, and the accounted records the oracle needs.</summary>
    private (byte[] Snapshot, IReadOnlyList<AccountedRecord> Records) SnapshotForUpload(int records) =>
        _snapshotFactory is not null
            ? _snapshotFactory(records)
            : _accounted ? AccountingSnapshot(records) : (Snapshot(records), []);

    /// <summary>
    /// The first records of the pilot grouped dataset, with the fixture package's header:
    /// the dataset's per-group structure gives one group larger than a block, and its amounts
    /// and group keys are kept for the independent oracle.
    /// </summary>
    private static (byte[] Snapshot, IReadOnlyList<AccountedRecord> Records) AccountingSnapshot(int records) =>
        MultiBlockFixtures.Snapshot(records);
    private static string StorageRoot() => System.IO.Path.Combine(RepositoryPaths.Root, ".storage");

    /// <summary>
    /// The one-shot marker the test-only assembly interruption writes under the shared
    /// storage root once it has fired. Its name mirrors the seam's own
    /// (<c>AssemblyInterruption.MarkerName</c>: the SHA-256 of the output id), so the
    /// case can wait for the interruption before resuming the worker.
    /// </summary>
    /// <param name="outputId">The output the assembly is for.</param>
    public static string InterruptionMarker(string outputId) =>
        System.IO.Path.Combine(
            StorageRoot(),
            "assembly-interrupt",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(outputId))) + ".marker");

    /// <summary>The handles one composition exposes to the stage's cases.</summary>
    public sealed record Composition(
        DistributedApplication App,
        NpgsqlDataSource DataSource,
        IObjectStore Storage,
        PostgresUploadStore Uploads,
        PostgresTemplateVersionStore Versions,
        string MergeWorkerContainerName) : IAsyncDisposable
    {
        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await DataSource.DisposeAsync();
        }
    }

    /// <summary>A job as it ended, with its one artifact's values.</summary>
    public sealed record EndedJob(
        string Status,
        string? ErrorCode,
        string? OutputId,
        string? OutputStatus,
        string? OutputErrorCode,
        string? OutputErrorReason,
        string? ArtifactId,
        string? ObjectKey,
        string? Sha256,
        long? SizeBytes);

    /// <summary>One part commit as the end-to-end check reads it.</summary>
    public sealed record PartRow(int BlockIndex, string ObjectKey, string Sha256, long SizeBytes, int PageCount);
}

/// <summary>The reserved numbering rectangle, converted to the points the reader measures in.</summary>
internal sealed record NumberingBounds(double LeftPt, double TopPt, double WidthPt, double HeightPt)
{
    /// <summary>How many points one millimetre is.</summary>
    public const double PointsPerMillimetre = 72d / 25.4d;

    /// <summary>The rectangle a template declares in millimetres.</summary>
    public static NumberingBounds FromMillimetres(double left, double top, double width, double height) => new(
        left * PointsPerMillimetre,
        top * PointsPerMillimetre,
        width * PointsPerMillimetre,
        height * PointsPerMillimetre);
}
