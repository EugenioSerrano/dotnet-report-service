using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Resolution;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ManifestReader = ReportService.TemplateSource.Manifest.TemplateManifestReader;

namespace ReportService.TemplateSource.Tests.Resolution;

/// <summary>
/// The adapter between the take and the resolution's seam: the take's four
/// answers, and nothing else. The three answers that carry a record — the take
/// that wrote it, the version another job had already taken, and the version the
/// take met while the branch changed under it — are one and the same answer to a
/// resolution, a refusal keeps its own code, text and retryable flag, and a
/// failure the take does not turn into an answer is never caught here. The
/// adapter maps nothing else: no retry, no logging, no decision of its own.
/// </summary>
public sealed class TemplateTakeAdapterTests : IDisposable
{
    private const string Consumer = "billing-system";
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "report-service-take-adapter",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryTemplateSource _source = new();
    private readonly InMemoryTemplateVersionStore _versions = new();
    private readonly TemplateSourceOptions _options = new()
    {
        Repository = "report-service/templates",
        Branch = "main",
        AllowInsecureLoopback = true,
    };

    private readonly FilesystemObjectStore _objects;
    private readonly TemplateVersionConflict _conflict;

    public TemplateTakeAdapterTests()
    {
        Directory.CreateDirectory(_root);
        _objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = Path.Combine(_root, "storage"), CreateIfMissing = true },
            Path.Combine(_root, "storage"),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        _conflict = new TemplateVersionConflict(NullLogger<TemplateVersionConflict>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task AVersionTheTakeWroteIsOneAnswerCarryingItsRecord()
    {
        _source.SeedPackage(Template, Version, Package(Manifest()));
        var commit = await _source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var attempt = await Adapter().TakeAsync(
            Template, Version, Consumer, commit, "job-0001", TestContext.Current.CancellationToken);

        var taken = Assert.IsType<TakenAttempt>(attempt);
        Assert.Equal(Template, taken.Record.TemplateId);
        Assert.Equal(Version, taken.Record.Version);
        Assert.Equal(Consumer, taken.Record.OwnerConsumerId);
        Assert.Equal(commit.CommitSha, taken.Record.CommitSha);
    }

    [Fact]
    public async Task AVersionAnotherJobAlreadyTookIsTheSameAnswer()
    {
        _versions.Seed(TemplateVersionRecords.Sample(Template, Version));

        var attempt = await Adapter().TakeAsync(
            Template, Version, Consumer, commit: null, "job-0002", TestContext.Current.CancellationToken);

        var taken = Assert.IsType<TakenAttempt>(attempt);
        Assert.Equal(Version, taken.Record.Version);
    }

    [Fact]
    public async Task AVersionTakenWhileTheBranchChangedIsASuccessCarryingTheRecord()
    {
        _source.SeedPackage(Template, Version, Package(Manifest()));
        var existing = TemplateVersionRecords.Sample(Template, Version, packageSha256: new string('b', 64));

        var attempt = await Adapter(new ExistingStore(_versions, existing)).TakeAsync(
            Template, Version, Consumer, commit: null, "job-0003", TestContext.Current.CancellationToken);

        var taken = Assert.IsType<TakenAttempt>(attempt);
        Assert.Equal(new string('b', 64), taken.Record.PackageSha256);
    }

    [Fact]
    public async Task ARefusalPassesThroughWithItsCodeItsTextAndItsFlag()
    {
        _source.SeedPackage(Template, Version, Package(Manifest(version: "9.9.9")));

        var attempt = await Adapter().TakeAsync(
            Template, Version, Consumer, commit: null, null, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedAttempt>(attempt);
        Assert.Equal(TemplateTakeCodes.TemplateInvalid, refused.Code);
        Assert.False(refused.Retryable);
        Assert.Contains("9.9.9", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureTheTakeDoesNotAnswerIsNotCaught()
    {
        var store = new ThrowingStore(new InvalidOperationException("the store went away"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Adapter(store).TakeAsync(
                Template, Version, Consumer, commit: null, null, TestContext.Current.CancellationToken));

        Assert.Equal("the store went away", failure.Message);
    }

    private TemplateTakeAdapter Adapter(ITemplateVersionStore? store = null) =>
        new(new TemplateTake(
            _source,
            store ?? _versions,
            _objects,
            new ManifestReader(),
            _conflict,
            checks: [],
            new TemplateTakeMetrics(),
            _clock,
            Options.Create(_options),
            NullLogger<TemplateTake>.Instance));

    private static Dictionary<string, byte[]> Package(string manifest) => new(StringComparer.Ordinal)
    {
        ["template.yaml"] = Encoding.UTF8.GetBytes(manifest),
        ["schema.json"] = Encoding.UTF8.GetBytes("{}"),
        ["html/row.html.sbn"] = Encoding.UTF8.GetBytes("<tr></tr>"),
    };

    private static string Manifest(string version = Version) =>
        $"""
        manifestVersion: "1.0"
        id: {Template}
        version: "{version}"
        owner: {Consumer}
        shared: false
        engine: scriban
        engineVersion: "7.5.1"
        dataContract:
          version: "1.0"
          profile: header-items-v1
          schema: schema.json
        outputs:
          html:
            parts:
              documentStart: html/document-start.html.sbn
              row: html/row.html.sbn
              documentEnd: html/document-end.html.sbn
        """;

    /// <summary>A store whose insert meets the record another take committed first, with other bytes.</summary>
    private sealed class ExistingStore(ITemplateVersionStore inner, TemplateVersionRecord existing) : ITemplateVersionStore
    {
        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            inner.FindAsync(templateId, version, cancellationToken);

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            inner.ListByTemplateAsync(templateId, cancellationToken);

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) =>
            Task.FromResult(TemplateVersionInsert.Existing(existing));
    }

    /// <summary>A store that fails with a cause the take does not turn into one of its answers.</summary>
    private sealed class ThrowingStore(Exception failure) : ITemplateVersionStore
    {
        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            Task.FromException<TemplateVersionRecord?>(failure);

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<TemplateVersionRecord>>(failure);

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) =>
            Task.FromException<TemplateVersionInsert>(failure);
    }
}
