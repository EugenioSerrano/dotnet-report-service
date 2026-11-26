using System.Formats.Tar;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Data;
using ReportService.Jobs;
using ReportService.Pdf.Verification;
using ReportService.Rendering;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Templates;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Pdf.Rendering;
using Temporalio.Testing;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The render step as a plain class: what it answers when the block was printed
/// already, what it refuses when the job's own state cannot be used, and one whole
/// render with doubles of the store, the storage and the renderer - which proves the
/// object key it writes, the commit it records and the answer it gives.
/// </summary>
public sealed class RenderPdfBlockActivityTests : IDisposable
{
    private const string JobId = "job-1";
    private const string OutputId = "output-1";
    private readonly string root = Path.Combine(Path.GetTempPath(), "render-step-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheQueueRunsOneActivityAtATimeAndRetriesThreeTimes()
    {
        Assert.Equal("report-pdf-rendering", RenderPdfBlockOptions.Queue);
        Assert.Equal(TimeSpan.FromSeconds(5), RenderPdfBlockOptions.HeartbeatEvery);
        Assert.Equal(TimeSpan.FromSeconds(30), RenderPdfBlockOptions.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromSeconds(300), RenderPdfBlockOptions.StartToClose);
        Assert.Equal(3, RenderPdfBlockOptions.MaximumAttempts);

        var options = RenderPdfBlockOptions.Create();
        Assert.Equal("report-pdf-rendering", options.TaskQueue);
        Assert.Equal(3, options.RetryPolicy!.MaximumAttempts);
        Assert.Contains("PAGINATION_FAILED", RenderPdfBlockOptions.NonRetryableErrorTypes);
        Assert.Contains("UNSUPPORTED_CAPABILITY", RenderPdfBlockOptions.NonRetryableErrorTypes);
        Assert.Contains("QUOTA_EXCEEDED", RenderPdfBlockOptions.NonRetryableErrorTypes);
        Assert.Contains("EXECUTION_FAILED", RenderPdfBlockOptions.NonRetryableErrorTypes);
        Assert.Contains("OUTPUT_LIMIT_EXCEEDED", RenderPdfBlockOptions.NonRetryableErrorTypes);

        // The whole list is asserted, so a further entry or a removal is red: the commit-time
        // ceiling adds its own non-retryable code and nothing else.
        string[] delivered =
        [
            "PAGINATION_FAILED",
            "UNSUPPORTED_CAPABILITY",
            "QUOTA_EXCEEDED",
            "EXECUTION_FAILED",
            "OUTPUT_LIMIT_EXCEEDED",
        ];
        Assert.Equal(delivered, RenderPdfBlockOptions.NonRetryableErrorTypes);
        Assert.Equal(delivered, options.RetryPolicy!.NonRetryableErrorTypes);
    }

    [Fact]
    public void TheAttemptIdentityIsOneStableDigestPerAttempt()
    {
        var first = RenderPdfBlockOptions.AttemptId("job", "run", "activity", 1);
        var second = RenderPdfBlockOptions.AttemptId("job", "run", "activity", 1);
        var other = RenderPdfBlockOptions.AttemptId("job", "run", "activity", 2);

        Assert.Equal(24, first.Length);
        Assert.Equal("da3a81fb2ffbb2550df8b0f7", first);   // the first 96 bits of the documented digest
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.All(first, character => Assert.True(char.IsAsciiHexDigitLower(character)));
    }

    [Fact]
    public void TheAttemptDirectoryIsCreatedAndRemoved()
    {
        string path;
        using (var directory = WorkerTemporaryDirectory.Create(Path.Combine(root, "work")))
        {
            path = directory.Path;
            Assert.True(Directory.Exists(path));
            File.WriteAllText(Path.Combine(path, "printed.pdf"), "x");
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ABlockThatWasCommittedAlreadyIsAnsweredWithoutRendering()
    {
        var commit = new PartCommit(OutputId, 0, "outputs/job-1/output-1/attempt", new string('a', 64), 10, 2, new string('b', 64), DateTimeOffset.UnixEpoch);
        var activity = Activity(jobs: new FakeJobStore { Part = commit }, renderer: new FakeRenderer());

        var result = await new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0)));

        Assert.Equal(commit.ObjectKey, result.PartKey);
        Assert.Equal(commit.Sha256, result.Sha256);
        Assert.Equal(2, result.PageCount);
    }

    [Fact]
    public async Task AJobWhoseTemplateWasNeverResolvedIsRefused()
    {
        var activity = Activity(jobs: new FakeJobStore { Reference = null }, renderer: new FakeRenderer());

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task AJobWhoseVersionWasNeverTakenIsRefused()
    {
        var jobs = new FakeJobStore { Reference = ReferenceOf(Pin()) };
        var activity = Activity(jobs: jobs, versions: new FakeVersionStore { Record = null }, renderer: new FakeRenderer());

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
    }

    [Fact]
    public async Task AVersionThatNoLongerMatchesThePinIsRefused()
    {
        var jobs = new FakeJobStore { Reference = ReferenceOf(Pin(packageSha256: new string('c', 64))) };
        var activity = Activity(jobs: jobs, versions: new FakeVersionStore { Record = Record(new string('d', 64), default, 0) }, renderer: new FakeRenderer());

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
    }

    [Fact]
    public async Task AJobWithoutAPartitionIsRefused()
    {
        var jobs = new FakeJobStore { Reference = ReferenceOf(Pin()) };
        var activity = Activity(jobs: jobs, versions: new FakeVersionStore { Record = Record(Pin().PackageSha256, default, 0) }, renderer: new FakeRenderer());

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
    }

    [Fact]
    public async Task ABlockThatPrintsAndCommitsAnswersWithItsCommit()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var renderer = new FakeRenderer();
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, renderer, storage.Store);

        var result = await new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0)));

        Assert.StartsWith("outputs/", result.PartKey, StringComparison.Ordinal);
        Assert.Equal(2, result.PageCount);
        Assert.NotNull(jobs.Committed);
        Assert.Equal(result.PartKey, jobs.Committed!.ObjectKey);
        Assert.Equal(pin.PackageSha256, jobs.Committed.TemplatePackageSha256);
        Assert.True(renderer.Called);
    }

    /// <summary>
    /// US-010 AC-2, hermetically: a render that is cancelled while it works stops at its safe
    /// point, and the step releases the attempt's temporary directory on the way out. Nothing is
    /// committed and nothing is published from a cancelled render, so no partial or corrupt
    /// artifact exists (ADR-012 §3.10(3)(4)). The cancellation is the engine's own: the activity
    /// host is asked to cancel the attempt the way the engine does.
    /// </summary>
    [Fact]
    public async Task ACancelledRenderStopsReleasesItsDirectoryAndCommitsNothing()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var renderer = new BlockingRenderer();
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, renderer, storage.Store);

        var environment = new ActivityEnvironment();
        var running = environment.RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0)));
        await renderer.Started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        environment.Cancel();

        await Assert.ThrowsAnyAsync<Exception>(() => running);
        Assert.True(renderer.ObservedCancellation, "the render had to observe the cancellation its engine delivered");
        Assert.NotNull(renderer.WorkDirectory);
        Assert.False(Directory.Exists(renderer.WorkDirectory), "the attempt's temporary directory had to be released");
        Assert.Null(jobs.Committed);
    }

    [Fact]
    public async Task ASecondBlockReadsItsRecordsWithTheGlobalOrdinalsItsIndexDeclares()
    {
        var storage = await TwoBlockStorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(
                JobId,
                2,
                2,
                6,
                storage.First.SizeBytes + storage.Second.SizeBytes,
                storage.Index.Key,
                storage.Index.Sha256,
                storage.Index.SizeBytes,
                DateTimeOffset.UnixEpoch),
        };
        var renderer = new CapturingRenderer();
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, renderer, storage.Store);

        var result = await new ActivityEnvironment().RunAsync(() =>
            activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 1)));

        // The block document numbers its records from one, locally; the block's index
        // entry declares that the snapshot's first ordinal here is 4. The records the
        // composition and the block facts carry must be the global interval 4..6, so the
        // verifier's row coverage sees the interval the index declares.
        Assert.NotNull(renderer.Request);
        var request = renderer.Request!;
        Assert.Equal(1, request.BlockIndex);
        Assert.Equal([4L, 5L, 6L], request.Composition.Records.Select(item => item.Ordinal));
        Assert.Equal([4L, 5L, 6L], request.Block.Records.Select(item => item.Ordinal));
        Assert.Equal(4, request.Block.FirstRecordOrdinal);
        Assert.Equal(4L, request.Composition.Block.FirstRecordOrdinal);
        Assert.Equal(
            6L,
            request.Composition.Block.FirstRecordOrdinal + request.Composition.Block.RecordCount - 1);
        Assert.StartsWith("parts/", result.PartKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// The commit the step records carries what produced the presentation, in the same
    /// transaction: the job's fixed culture, zone and clock, the engine the version declares
    /// and the version that engine runs, and the globalisation data of the process that
    /// printed it. The four identifiers reach the printed line as names, never a value.
    /// </summary>
    [Fact]
    public async Task ACommitRecordsThePresentationAndTheLineNamesIt()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        using var logs = new CapturingLogs();
        var activity = Activity(
            jobs,
            new FakeVersionStore { Record = record },
            new FakeRenderer(),
            storage.Store,
            provenance: new GlobalisationProvenance("72.1.0.4", "10.0.26100.9818"),
            loggers: logs.Factory);

        await new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0)));

        var presentation = jobs.Committed!.Presentation;
        Assert.NotNull(presentation);
        Assert.Equal("es-AR", presentation!.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", presentation.TimeZone);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero), presentation.Clock);
        Assert.Equal("scriban", presentation.Engine);
        Assert.Equal("7.5.1", presentation.EngineVersion);
        Assert.Equal("72.1.0.4", presentation.IcuVersion);
        Assert.Equal("10.0.26100.9818", presentation.TzDataVersion);

        var line = Assert.Single(logs.Lines, text => text.Contains("printed:", StringComparison.Ordinal));
        Assert.Contains("engine scriban 7.5.1", line, StringComparison.Ordinal);
        Assert.Contains("culture es-AR", line, StringComparison.Ordinal);
        Assert.Contains("time zone America/Argentina/Buenos_Aires", line, StringComparison.Ordinal);
        Assert.DoesNotContain("10.00", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The engine the version declares is refused before anything is composed, and the step
    /// answers with that refusal's own code, non-retryably, with no commit and nothing under
    /// the output namespace. The two codes a version can be refused with are the decision's.
    /// </summary>
    /// <param name="engine">The engine name the manifest declares.</param>
    [Theory]
    [InlineData("RAZOR")]
    [InlineData("erlang")]
    public async Task AnEngineTheDeploymentDoesNotOfferIsRefusedWithItsOwnCode(string engine)
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var renderer = new EngineRefusingRenderer(engine);
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, renderer, storage.Store);

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("UNSUPPORTED_CAPABILITY", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("reason=engine-unsupported", failure.Message, StringComparison.Ordinal);
        Assert.True(renderer.Called, "the engine decision belongs to the renderer, so the refusal arrives from it");
        Assert.Null(jobs.Committed);
        Assert.False(Directory.Exists(Path.Combine(root, "store", "outputs")));
    }

    /// <summary>
    /// A version that declares no engine reaches the renderer's own decision, which refuses it
    /// as a defect of the take rather than as a capability the deployment lacks.
    /// </summary>
    [Fact]
    public async Task AVersionThatDeclaresNoEngineIsRefusedAsATakeDefect()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var renderer = new EngineRefusingRenderer(string.Empty);
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, renderer, storage.Store);

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("TEMPLATE_INVALID", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("reason=engine-undeclared", failure.Message, StringComparison.Ordinal);
        Assert.True(renderer.Called, "the engine decision belongs to the renderer, so the refusal arrives from it");
        Assert.Null(jobs.Committed);
    }

    /// <summary>
    /// The step takes the engine decision itself, before the package is prepared: the pin and
    /// the taken copy disagreeing about the engine is that decision's refusal, and it is a
    /// defect of the take. It ends the block with D2's code and reason, non-retryably, with
    /// nothing composed, committed or written under the output namespace.
    /// </summary>
    [Fact]
    public async Task AnEngineThePinDisagreesWithIsRefusedBeforeComposing()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256, TemplateEngine.Handlebars);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var renderer = new FakeRenderer();
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, renderer, storage.Store);

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("TEMPLATE_INVALID", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("reason=engine-mismatch", failure.Message, StringComparison.Ordinal);
        Assert.Contains("declared=scriban", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pinned=handlebars", failure.Message, StringComparison.Ordinal);
        Assert.False(renderer.Called, "the step's own decision refuses the block, so the renderer never sees it");
        Assert.Null(jobs.Committed);
        Assert.False(Directory.Exists(Path.Combine(root, "store", "outputs")));
    }

    /// <summary>
    /// A version whose declared engine version names another major than the one its adapter
    /// runs is a capability this deployment does not have. The step's own decision refuses it
    /// before composing, with the code D2 gives it and nothing written.
    /// </summary>
    [Fact]
    public async Task AnEngineVersionTheDeploymentDoesNotRunIsRefusedBeforeComposing()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size) with { EngineVersion = "8.0.0" };
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var renderer = new FakeRenderer();
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, renderer, storage.Store);

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("UNSUPPORTED_CAPABILITY", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("reason=engine-version", failure.Message, StringComparison.Ordinal);
        Assert.Contains("declared=8.0.0", failure.Message, StringComparison.Ordinal);
        Assert.Contains("offered=7.5.1", failure.Message, StringComparison.Ordinal);
        Assert.False(renderer.Called, "the step's own decision refuses the block, so the renderer never sees it");
        Assert.Null(jobs.Committed);
        Assert.False(Directory.Exists(Path.Combine(root, "store", "outputs")));
    }

    /// <summary>
    /// A deployment whose built engine set does not offer the engine a taken version declares
    /// cannot compose the block. The step's own decision refuses it before composing, with
    /// <c>UNSUPPORTED_CAPABILITY</c> and the offered names, and never a value of the data.
    /// </summary>
    [Fact]
    public async Task AnEngineTheDeploymentDoesNotOfferIsRefusedBeforeComposing()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var renderer = new FakeRenderer();
        var deployment = new TemplateEngineAdapterRegistry([new HandlebarsTemplateAdapter(), new FluidTemplateAdapter()]);
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, renderer, storage.Store, engines: deployment);

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("UNSUPPORTED_CAPABILITY", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("reason=engine-unsupported", failure.Message, StringComparison.Ordinal);
        Assert.Contains("offered=handlebars,liquid", failure.Message, StringComparison.Ordinal);
        Assert.False(renderer.Called, "the step's own decision refuses the block, so the renderer never sees it");
        Assert.Null(jobs.Committed);
        Assert.False(Directory.Exists(Path.Combine(root, "store", "outputs")));
    }

    /// <summary>
    /// A value or a format the version cannot present ends the block before anything is
    /// printed, with the refusal's own code and reason - never a value, never the format's
    /// expansion - and nothing is committed.
    /// </summary>
    /// <param name="code">The code the surface refused with.</param>
    /// <param name="reason">The reason word.</param>
    /// <param name="function">The function that refused.</param>
    [Theory]
    [InlineData("DATA_INVALID", "number-out-of-range", "format_number")]
    [InlineData("DATA_INVALID", "type-mismatch", "format_date")]
    [InlineData("TEMPLATE_INVALID", "format-string", "format_number")]
    public async Task APresentationTheVersionCannotProduceIsRefusedWithItsOwnCode(string code, string reason, string function)
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, new RefusingPresentationRenderer(code, reason, function), storage.Store);

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal(code, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("reason=" + reason, failure.Message, StringComparison.Ordinal);
        Assert.Contains("function=" + function, failure.Message, StringComparison.Ordinal);
        Assert.Null(jobs.Committed);
        Assert.False(Directory.Exists(Path.Combine(root, "store", "outputs")));
    }

    /// <summary>
    /// The part-commit fault a test host installs is absent from the product's own
    /// composition and no public member accepts one: the seam is reachable only from the
    /// tests that name it, and a process that starts without it commits every block it prints.
    /// </summary>
    [Fact]
    public async Task ThePartCommitFaultIsAbsentFromTheProductsComposition()
    {
        // The seam itself is internal, no public member of the step takes one, and no public
        // type of the assembly implements it: a process cannot be handed one from outside.
        Assert.DoesNotContain(typeof(RenderPdfBlockActivity).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(PartCommitFault));
        Assert.DoesNotContain(
            typeof(RenderPdfBlockActivity).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
            property => property.PropertyType == typeof(PartCommitFault));
        Assert.DoesNotContain(typeof(RenderPdfBlockActivity).Assembly.GetTypes(),
            type => type.IsPublic && typeof(PartCommitFault).IsAssignableFrom(type));

        // A step built exactly as the product builds it commits without consulting the seam.
        var activity = Activity(new FakeJobStore(), new FakeVersionStore());
        Assert.Null(activity.PartCommitFault);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ABlockThatPrintsReportsItsFourPhasesAndItsVerdict()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var identity = MeterIdentity.ForHost("ReportService.Tests.RenderPhases");
        using var metrics = new PdfRenderingMetrics(identity);
        using var recorded = new RecordedMeasurements(identity);
        var renderer = new FakeRenderer(new PrintOutcome("printed.pdf", AbortedRequests: 2, ConsoleErrors: 0, TimeSpan.FromSeconds(1)));
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, renderer, storage.Store, metrics);

        await new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0)));

        // The composition, the print, the verification and the store each reported a duration;
        // the verdict passed with no class, the block holds two pages, and the two requests the
        // document made that were aborted are counted.
        var phases = recorded.Of(PdfRenderingMetrics.BlockDurationInstrumentName);
        Assert.Equal(4, phases.Count);
        Assert.Equal(
            [PdfRenderingMetrics.ComposePhase, PdfRenderingMetrics.PrintPhase, PdfRenderingMetrics.VerifyPhase, PdfRenderingMetrics.StorePhase],
            phases.Select(measurement => measurement.Tag("phase")));
        var outcome = Assert.Single(recorded.Of(PdfRenderingMetrics.VerificationOutcomesInstrumentName));
        Assert.Equal(PdfRenderingMetrics.PassResult, outcome.Tag("result"));
        Assert.Equal(PdfRenderingMetrics.NoClass, outcome.Tag("class"));
        Assert.Equal(2, Assert.Single(recorded.Of(PdfRenderingMetrics.PagesInstrumentName)).Value);
        Assert.Equal(2, Assert.Single(recorded.Of(PdfRenderingMetrics.AbortedRequestsInstrumentName)).Value);
    }

    [Fact]
    public async Task ABlockOutsideTheJobsPartitionIsRefused()
    {
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(Pin()),
            Partition = new PartitionRecord(JobId, 1, 1, 1, 0, "index/job-1/0/a", new string('a', 64), 1, DateTimeOffset.UnixEpoch),
        };
        var activity = Activity(jobs: jobs, versions: new FakeVersionStore { Record = Record(Pin().PackageSha256, default, 0) }, renderer: new FakeRenderer());

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 3))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    [Fact]
    public async Task APartitionWhoseIndexPageIsNotThereIsRefused()
    {
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(Pin()),
            Partition = new PartitionRecord(JobId, 1, 1, 1, 0, "index/job-1/0/missing", new string('a', 64), 1, DateTimeOffset.UnixEpoch),
        };
        var activity = Activity(jobs: jobs, versions: new FakeVersionStore { Record = Record(Pin().PackageSha256, default, 0) }, renderer: new FakeRenderer());

        await Assert.ThrowsAnyAsync<Exception>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));
    }

    [Fact]
    public async Task ABlockWhosePackageDeclaresNoFieldIsRefusedBeforeItIsComposed()
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var withoutTheDeclaration = record with
        {
            Manifest = record.Manifest with
            {
                Outputs = record.Manifest.Outputs! with
                {
                    Pdf = record.Manifest.Outputs!.Pdf! with { Verification = null },
                },
            },
        };
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var printer = new PrinterThatMustNotPrint();
        var renderer = new BlockRenderer(
            new BlockComposer(TimeProvider.System),
            printer,
            new BlockVerifier(),
            TemplateEngineAdapters.Registry());
        var activity = Activity(jobs, new FakeVersionStore { Record = withoutTheDeclaration }, renderer, storage.Store);

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        // A taken copy whose manifest declares no field it prints as it is cannot be bound to
        // its data: the step fails closed with the print class, before composing, so nothing
        // is printed, committed or written under the output namespace.
        Assert.Equal("PAGINATION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Null(jobs.Committed);
        Assert.False(
            Directory.Exists(Path.Combine(root, "store", "outputs")),
            "a refused block wrote an object under the output namespace");
        Assert.False(printer.Called, "a package that declares no field it prints as it is must be refused before printing");
    }

    [Fact]
    public async Task ATakenCopyThatIsNotThereIsRefused()
    {
        var storage = await StorageAsync();
        var pin = Pin(new string('9', 64));
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var record = Record(pin.PackageSha256, ObjectKey.FromSegments("templates", "consumer", "integrity-table", "1.0.0", new string('9', 64)), 10);
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, new FakeRenderer(), storage.Store);

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
    }

    [Theory]
    [InlineData("culture")]
    [InlineData("time-zone")]
    [InlineData("clock")]
    public async Task ABlockWhoseJobHasNoUsablePresentationMemberIsRefused(string member)
    {
        var storage = await StorageAsync();
        var package = await PackageAsync(storage.Store);
        var pin = Pin(package.Sha256);
        var record = Record(pin.PackageSha256, package.Key, package.Size);
        var jobs = new FakeJobStore
        {
            Reference = ReferenceOf(pin),
            Partition = new PartitionRecord(JobId, 1, 1, 1, storage.Block.SizeBytes, storage.Index.Key, storage.Index.Sha256, storage.Index.SizeBytes, DateTimeOffset.UnixEpoch),
        };
        var activity = Activity(jobs, new FakeVersionStore { Record = record }, new RefusingRenderer(member), storage.Store);

        var failure = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(JobId, OutputId, 0))));

        // The refusal happens before composing: it is non-retryable, it names the job member
        // and never a value, and nothing was written under the output namespace or committed.
        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains(member, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'", failure.Message, StringComparison.Ordinal);
        Assert.Null(jobs.Committed);
        Assert.False(Directory.Exists(Path.Combine(root, "store", "outputs")));
    }

    private RenderPdfBlockActivity Activity(
        FakeJobStore jobs,
        FakeVersionStore? versions = null,
        IBlockRenderer? renderer = null,
        IObjectStore? store = null,
        PdfRenderingMetrics? metrics = null,
        GlobalisationProvenance? provenance = null,
        ILoggerFactory? loggers = null,
        TemplateEngineAdapterRegistry? engines = null)
    {
        store ??= FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = Path.Combine(root, "store"), CreateIfMissing = true },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

        return new RenderPdfBlockActivity(
            jobs,
            versions ?? new FakeVersionStore(),
            store,
            new TakenTemplatePackageReader(store),
            new PreparedTemplateCache(),
            engines ?? TemplateEngineAdapters.Registry(),
            renderer ?? new FakeRenderer(),
            metrics ?? new PdfRenderingMetrics(MeterIdentity.ForHost(TelemetryDefaults.MeterName)),
            TimeProvider.System,
            loggers ?? NullLoggerFactory.Instance,
            loggers?.CreateLogger<RenderPdfBlockActivity>() ?? NullLogger<RenderPdfBlockActivity>.Instance,
            Path.Combine(root, "scratch"),
            provenance);
    }

    private static TemplatePin Pin(string? packageSha256 = null, TemplateEngine engine = TemplateEngine.Scriban) =>
        new(
            "integrity-table",
            "1.0.0",
            new string('e', 40),
            null,
            null,
            packageSha256 ?? new string('f', 64),
            engine,
            "7.5.1",
            "schema.json",
            DateTimeOffset.UnixEpoch);

    private static TemplateReferenceRecord ReferenceOf(TemplatePin pin) =>
        new(JobId, pin.TemplateId, TemplateReferenceForm.Exact) { RequestedVersion = pin.ResolvedVersion, Pin = pin };

    private static TemplateVersionRecord Record(string packageSha256, ObjectKey objectKey, long packageSize)
    {
        var manifest = new TemplateManifest
        {
            ManifestVersion = "1.0",
            Id = "integrity-table",
            Version = "1.0.0",
            Engine = TemplateEngine.Scriban,
            DeclaredEngineVersion = "7.5.1",
            Outputs = new TemplateOutputs
            {
                Html = new TemplateHtmlOutput
                {
                    Parts = new TemplateParts
                    {
                        DocumentStart = "html/document-start.html.sbn",
                        Row = "html/row.html.sbn",
                        DocumentEnd = "html/document-end.html.sbn",
                    },
                    Styles = ["html/styles.css"],
                },
                Pdf = new TemplatePdfOutput
                {
                    Paper = "A4",
                    Orientation = "portrait",
                    PageTotals = new TemplatePageTotals { Enabled = false },
                    MarginMm = new TemplateMargins { Top = 15, Right = 12, Bottom = 18, Left = 12 },
                    Verification = new TemplatePdfVerification
                    {
                        Anchors = new TemplatePdfAnchors { Header = ["$.reportTitle"], Item = ["$.description"] },
                    },
                },
            },
            LocaleDefaults = new TemplateLocaleDefaults { Culture = "es-AR", TimeZone = "America/Argentina/Buenos_Aires" },
        };

        return new TemplateVersionRecord(
            "integrity-table",
            "1.0.0",
            "consumer",
            Shared: true,
            "1.0",
            TemplateEngine.Scriban,
            "7.5.1",
            "schema.json",
            new string('e', 40),
            new string('e', 40),
            packageSha256,
            packageSize,
            FileCount: 4,
            objectKey,
            manifest,
            "{}",
            DateTimeOffset.UnixEpoch);
    }

    /// <summary>The block and its index as the data step leaves them, written to a filesystem store.</summary>
    private async Task<Storage> StorageAsync()
    {
        var store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = Path.Combine(root, "store"), CreateIfMissing = true },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

        var block = Encoding.UTF8.GetBytes(
            "{\"contractVersion\":\"1.0\",\"header\":{\"reportTitle\":\"Statement\"},\"items\":[{\"sequence\":\"1\",\"description\":\"one\",\"amount\":\"10.00\"}]}");
        var blockKey = ObjectKey.FromSegments("block", JobId, "0", "a");
        var blockWrite = await store.WriteAsync(blockKey, new MemoryStream(block), CancellationToken.None);

        var indexPage = BlockIndexPage.Serialize(new BlockIndexPage(1,
            [new BlockIndexEntry(0, blockKey.ToString(), blockWrite.Value!.Sha256, blockWrite.Value.Size, 1, 1, block.Length)]));
        var indexKey = ObjectKey.FromSegments("index", JobId, "0", "a");
        var indexWrite = await store.WriteAsync(indexKey, new MemoryStream(indexPage), CancellationToken.None);

        return new Storage(
            store,
            new BlockIndexEntry(0, blockKey.ToString(), blockWrite.Value.Sha256, blockWrite.Value.Size, 1, 1, block.Length),
            new BlockIndexEntry(0, indexKey.ToString(), indexWrite.Value!.Sha256, indexWrite.Value.Size, 1, 1, indexPage.Length));
    }

    /// <summary>A two-block partition: block 0 holds records 1..3 and block 1 holds 4..6.</summary>
    private async Task<TwoBlockStorage> TwoBlockStorageAsync()
    {
        var store = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = Path.Combine(root, "store"), CreateIfMissing = true },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);

        var first = Encoding.UTF8.GetBytes(
            "{\"contractVersion\":\"1.0\",\"header\":{\"reportTitle\":\"Statement\"},\"items\":["
            + "{\"sequence\":\"1\",\"description\":\"one\",\"amount\":\"10.00\"},"
            + "{\"sequence\":\"2\",\"description\":\"two\",\"amount\":\"20.00\"},"
            + "{\"sequence\":\"3\",\"description\":\"three\",\"amount\":\"30.00\"}]}");
        var second = Encoding.UTF8.GetBytes(
            "{\"contractVersion\":\"1.0\",\"header\":{\"reportTitle\":\"Statement\"},\"items\":["
            + "{\"sequence\":\"4\",\"description\":\"four\",\"amount\":\"40.00\"},"
            + "{\"sequence\":\"5\",\"description\":\"five\",\"amount\":\"50.00\"},"
            + "{\"sequence\":\"6\",\"description\":\"six\",\"amount\":\"60.00\"}]}");
        var firstKey = ObjectKey.FromSegments("block", JobId, "0", "a");
        var secondKey = ObjectKey.FromSegments("block", JobId, "1", "a");
        var firstWrite = await store.WriteAsync(firstKey, new MemoryStream(first), CancellationToken.None);
        var secondWrite = await store.WriteAsync(secondKey, new MemoryStream(second), CancellationToken.None);
        var firstEntry = new BlockIndexEntry(0, firstKey.ToString(), firstWrite.Value!.Sha256, firstWrite.Value.Size, 1, 3, first.Length);
        var secondEntry = new BlockIndexEntry(1, secondKey.ToString(), secondWrite.Value!.Sha256, secondWrite.Value.Size, 4, 3, second.Length);

        var indexPage = BlockIndexPage.Serialize(new BlockIndexPage(1, [firstEntry, secondEntry]));
        var indexKey = ObjectKey.FromSegments("index", JobId, "1", "a");
        var indexWrite = await store.WriteAsync(indexKey, new MemoryStream(indexPage), CancellationToken.None);
        return new TwoBlockStorage(
            store,
            firstEntry,
            secondEntry,
            new BlockIndexEntry(0, indexKey.ToString(), indexWrite.Value!.Sha256, indexWrite.Value.Size, 1, 3, indexPage.Length));
    }

    /// <summary>The taken package as a tar, written to the store under a content-addressed key.</summary>
    private static async Task<TakenPackage> PackageAsync(IObjectStore store)
    {
        var folder = Path.Combine(RepositoryPaths.Root, "tests", "Support", "ReportService.Testing", "Templates", "integrity-table");
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                writer.WriteEntry(file, relative);
            }
        }

        var bytes = buffer.ToArray();
        buffer.Position = 0;
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var key = ObjectKey.FromSegments("templates", "consumer", "integrity-table", "1.0.0", sha256);
        await store.WriteAsync(key, buffer, CancellationToken.None);
        return new TakenPackage(key, sha256, bytes.LongLength);
    }

    private sealed record Storage(IObjectStore Store, BlockIndexEntry Block, BlockIndexEntry Index);

    private sealed record TwoBlockStorage(
        IObjectStore Store,
        BlockIndexEntry First,
        BlockIndexEntry Second,
        BlockIndexEntry Index);

    private sealed record TakenPackage(ObjectKey Key, string Sha256, long Size);
}

/// <summary>The store as the step reads it: the four calls it makes, and nothing else.</summary>
internal sealed class FakeJobStore : IJobStore
{
    public TemplateReferenceRecord? Reference { get; init; }

    public PartitionRecord? Partition { get; init; }

    public PartCommit? Part { get; init; }

    public PartCommit? Commit { get; init; }

    public PartCommit? Committed { get; private set; }

    public List<AttemptCause> Causes { get; } = [];

    public Action? OnRecordCause { get; set; }

    public Task<PartCommit?> FindPartAsync(string outputId, int blockIndex, CancellationToken cancellationToken) =>
        Task.FromResult(Part);

    public Task<TemplateReferenceRecord?> FindTemplateReferenceAsync(string reportJobId, CancellationToken cancellationToken) =>
        Task.FromResult(Reference);

    public Task<PartitionRecord?> FindPartitionAsync(string reportJobId, CancellationToken cancellationToken) =>
        Task.FromResult(Partition);

    public Task<IdempotencyLookup> FindByIdempotencyKeyAsync(
        string consumerId,
        string idempotencyKey,
        RequestFingerprint fingerprint,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<JobStateRecord?> FindForWorkflowAsync(string reportJobId, CancellationToken cancellationToken) =>
        Task.FromResult<JobStateRecord?>(new JobStateRecord(
            new ReportJobRecord(reportJobId, "consumer", "upload", ReportJobStatus.Running, new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero), DateTimeOffset.UnixEpoch)
            {
                Culture = "es-AR",
                TimeZone = "America/Argentina/Buenos_Aires",
            },
            [],
            UploadStatus.Ready,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null));

    public Task<PartCommitResult> CommitPartAsync(PartCommit commit, CancellationToken cancellationToken)
    {
        Committed = commit;
        return Task.FromResult(new PartCommitResult(PartCommitOutcome.Committed, commit));
    }

    public Task<JobAcceptanceResult> AcceptAsync(JobAcceptance acceptance, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<JobFailureOutcome> FailJobAsync(string reportJobId, string code, string reason, string? message, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TemplatePinState> PinTemplateAsync(string reportJobId, TemplatePin pin, DateTimeOffset now, CancellationToken cancellationToken, TemplateLocaleDefaults? presentation) => throw new NotSupportedException();

    public Task<BeginInputWaitOutcome> BeginInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<EndInputWaitOutcome> EndInputWaitAsync(string reportJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> FindInputWaitingJobIdsAsync(string uploadId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TransitionJobResult> TransitionJobAsync(string reportJobId, IReadOnlyList<ReportJobStatus> from, ReportJobStatus to, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TransitionOutcome> TransitionOutputAsync(string outputId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PartitionCommitResult> CommitPartitionAsync(PartitionRecord partition, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PublishResult> PublishAsync(ArtifactRecord artifact, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<JobArtifactsView?> ListArtifactsAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ArtifactView?> FindArtifactAsync(string reportJobId, string artifactId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<OutputFailureOutcome> FailOutputAsync(string outputId, string code, string? reason, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<JobFinalizationResult> FinalizeJobAsync(string reportJobId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ReportJobStatusView?> FindStatusAsync(string reportJobId, string consumerId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ProgressWriteOutcome> RecordRecordsReadAsync(string reportJobId, long recordsRead, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<AttemptCauseOutcome> RecordAttemptCauseAsync(AttemptCause cause, CancellationToken cancellationToken)
    {
        OnRecordCause?.Invoke();
        Causes.Add(cause);
        return Task.FromResult(AttemptCauseOutcome.Recorded);
    }

    public Task<AttemptCause?> FindLastAttemptCauseAsync(string outputId, int blockIndex, CancellationToken cancellationToken) =>
        Task.FromResult(Causes
            .Where(cause => string.Equals(cause.OutputId, outputId, StringComparison.Ordinal) && cause.BlockIndex == blockIndex)
            .OrderByDescending(cause => cause.Attempt)
            .FirstOrDefault());
}

/// <summary>The version store as the step reads it.</summary>
internal sealed class FakeVersionStore : ITemplateVersionStore
{
    public TemplateVersionRecord? Record { get; init; }

    public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) => Task.FromResult(Record);

    public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>The renderer as the step calls it: it answers a verified document and writes nothing.</summary>
internal sealed class FakeRenderer(PrintOutcome? print = null) : IBlockRenderer{
    public bool Called { get; private set; }

    public async Task<BlockRenderOutcome> RenderAsync(BlockRenderRequest request, CancellationToken cancellationToken = default)
    {
        Called = true;
        Directory.CreateDirectory(request.WorkDirectory);
        var path = Path.Combine(request.WorkDirectory, "block-0.pdf");
        await File.WriteAllBytesAsync(path, "%PDF-1.4 printed"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new BlockRenderOutcome(
            path,
            bytes.LongLength,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            Print: print,
            new VerificationResult(true, null, 0, null, null, null, null, null, 2, 10, 1, 1));
    }
}

/// <summary>
/// The renderer that works until it is cancelled: it records the attempt's temporary directory
/// the step built and the token its caller passed, so a case can prove both that the render
/// observed the cancellation and that the step released the directory on the way out.
/// </summary>
internal sealed class BlockingRenderer : IBlockRenderer
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the step is inside the render.</summary>
    public Task Started => _started.Task;

    /// <summary>The attempt's temporary directory, as the step built the request.</summary>
    public string? WorkDirectory { get; private set; }

    /// <summary>Whether the render saw its token fire, which is where it stops.</summary>
    public bool ObservedCancellation { get; private set; }

    /// <inheritdoc />
    public async Task<BlockRenderOutcome> RenderAsync(
        BlockRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        WorkDirectory = request.WorkDirectory;
        Directory.CreateDirectory(request.WorkDirectory);
        _started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObservedCancellation = true;
            throw;
        }

        throw new InvalidOperationException("a render that was not cancelled cannot finish this case");
    }
}

/// <summary>
/// The renderer that keeps the request the step built for it: the regression that a
/// second block's records carry the snapshot's global ordinals reads the request
/// after the step returned, and answers a verified document so the step commits.
/// </summary>
internal sealed class CapturingRenderer : IBlockRenderer
{
    /// <summary>The request the step built, once it called the renderer.</summary>
    public BlockRenderRequest? Request { get; private set; }

    /// <inheritdoc />
    public async Task<BlockRenderOutcome> RenderAsync(
        BlockRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        Request = request;
        Directory.CreateDirectory(request.WorkDirectory);
        var path = Path.Combine(request.WorkDirectory, "block-" + request.BlockIndex + ".pdf");
        await File.WriteAllBytesAsync(path, "%PDF-1.4 printed"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new BlockRenderOutcome(
            path,
            bytes.LongLength,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            Print: null,
            new VerificationResult(true, null, request.BlockIndex, null, null, null, null, null, 2, 10, 1, 1));
    }
}

/// <summary>
/// The renderer as a job whose presentation cannot be fixed reaches it: it raises the
/// refusal for the case's member, and writes nothing.
/// </summary>
internal sealed class RefusingRenderer(string member) : IBlockRenderer
{
    public Task<BlockRenderOutcome> RenderAsync(BlockRenderRequest request, CancellationToken cancellationToken = default) =>
        throw new PresentationUnavailable(member);
}

/// <summary>
/// The renderer as a version whose engine cannot be used reaches it: it takes the one
/// decision over a manifest declaring <paramref name="engine"/> and raises the refusal the
/// activity has to map, before composing anything.
/// </summary>
internal sealed class EngineRefusingRenderer(string engine) : IBlockRenderer
{
    public bool Called { get; private set; }

    public Task<BlockRenderOutcome> RenderAsync(BlockRenderRequest request, CancellationToken cancellationToken = default)
    {
        Called = true;
        var decision = EngineResolution.Resolve(
            request.Manifest with { Engine = engine },
            pinEngine: null,
            TemplateEngineAdapters.Registry());
        throw new EngineRefusedException(decision);
    }
}

/// <summary>
/// The renderer as a value or a format the version cannot present reaches it: it raises the
/// typed refusal a presentation surface raises, and writes nothing.
/// </summary>
internal sealed class RefusingPresentationRenderer(string code, string reason, string function) : IBlockRenderer
{
    public Task<BlockRenderOutcome> RenderAsync(BlockRenderRequest request, CancellationToken cancellationToken = default) =>
        throw new PresentationRefusedException(new PresentationFailure(code, reason, function, RecordOrdinal: 1));
}

/// <summary>The lines a process wrote while a case ran, as the message each entry was formatted into.</summary>
internal sealed class CapturingLogs : IDisposable
{
    private readonly CaptureProvider _provider = new();

    public ILoggerFactory Factory { get; }

    public CapturingLogs() => Factory = LoggerFactory.Create(builder => builder.AddProvider(_provider));

    public IReadOnlyList<string> Lines => _provider.Lines;

    public void Dispose() => Factory.Dispose();

    private sealed class CaptureProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(CaptureProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (provider._lines)
                {
                    provider._lines.Add(formatter(state, exception));
                }
            }
        }
    }
}

/// <summary>
/// The printer as a refusal must leave it: a package refused before composing never
/// reaches the printer, so any call is the failure the case is watching for.
/// </summary>
internal sealed class PrinterThatMustNotPrint : IPdfPrinter
{
    public bool Called { get; private set; }

    public Task<PrintOutcome> PrintAsync(
        string document,
        PageRule pageRule,
        ITemplatePackageFiles files,
        string pdfPath,
        CancellationToken cancellationToken = default)
    {
        Called = true;
        return Task.FromResult(new PrintOutcome(pdfPath, AbortedRequests: 0, ConsoleErrors: 0, TimeSpan.Zero));
    }
}

/// <summary>What the product's meter published while a case ran, listened to by its own identity.</summary>
internal sealed class RecordedMeasurements : IDisposable
{
    private readonly MeterListener _listener = new();

    private readonly Lock _gate = new();

    private readonly List<Measurement> _measurements = [];

    public RecordedMeasurements(MeterIdentity identity)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, identity.Name, StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            Add(new Measurement(instrument.Name, value, tags.ToArray())));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            Add(new Measurement(instrument.Name, value, tags.ToArray())));
        _listener.Start();
    }

    public IReadOnlyList<Measurement> Of(string instrumentName)
    {
        lock (_gate)
        {
            return [.. _measurements.Where(measurement => string.Equals(measurement.Name, instrumentName, StringComparison.Ordinal))];
        }
    }

    public void Dispose() => _listener.Dispose();

    private void Add(Measurement measurement)
    {
        lock (_gate)
        {
            _measurements.Add(measurement);
        }
    }
}

/// <summary>One measurement as the meter published it.</summary>
internal sealed record Measurement(string Name, double Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)
{
    public string? Tag(string name) =>
        Tags.FirstOrDefault(tag => string.Equals(tag.Key, name, StringComparison.Ordinal)).Value as string;
}
