using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;

namespace ReportService.Workers.Pdf.Tests.Templates;

/// <summary>
/// One take, built on the real service with a source and a store held in memory
/// and an object store on a directory of this test's own: what the activity
/// cases need in order to run the activity itself, with nothing of the product
/// doubled.
/// </summary>
internal sealed class TakeHarness : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "report-service-pdf-worker-tests",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private readonly ILoggerFactory _loggerFactory;
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    public TakeHarness()
    {
        Directory.CreateDirectory(_root);
        Logs = new LogCapture();
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        Objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = Path.Combine(_root, "storage"), CreateIfMissing = true },
            Path.Combine(_root, "storage"),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        Options = new TemplateSourceOptions
        {
            Repository = "report-service/templates",
            Branch = "main",
            AllowInsecureLoopback = true,
        };
    }

    public LogCapture Logs { get; }

    public InMemoryTemplateSource Source { get; } = new();

    public InMemoryTemplateVersionStore Versions { get; } = new();

    public FilesystemObjectStore Objects { get; }

    public TemplateSourceOptions Options { get; }

    public TemplateTake Take(IEnumerable<ITemplateVersionCheck>? checks = null, ITemplateVersionStore? versions = null) =>
        new(
            Source,
            versions ?? Versions,
            Objects,
            new TemplateManifestReader(),
            new TemplateVersionConflict(_loggerFactory.CreateLogger(TemplateVersionConflict.LogCategory)),
            checks ?? [],
            new TemplateTakeMetrics(),
            _clock,
            Microsoft.Extensions.Options.Options.Create(Options),
            _loggerFactory.CreateLogger<TemplateTake>());

    /// <summary>Records a valid package of <paramref name="templateId"/> at <paramref name="version"/>.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="owner">The consumer the package's manifest names.</param>
    /// <param name="extra">One more key, when a case needs the text to differ.</param>
    public void Seed(string templateId, string version, string owner = "billing-system", string extra = "")
    {
        Source.SeedPackage(templateId, version, new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["template.yaml"] = Encoding.UTF8.GetBytes(
                $"""
                manifestVersion: "1.0"
                id: {templateId}
                version: "{version}"
                owner: {owner}
                shared: false
                engine: scriban
                engineVersion: "7.5.1"
                dataContract:
                  version: "1.0"
                  profile: header-items-v1
                  schema: schema.json
                {extra}
                """),
            ["schema.json"] = Encoding.UTF8.GetBytes("{}"),
            ["html/row.html.sbn"] = Encoding.UTF8.GetBytes("<tr></tr>"),
        });
    }

    /// <summary>The record of a version another process already took, with the hash the case names.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="packageSha256">The package hash the record carries.</param>
    public void SeedTaken(string templateId, string version, string packageSha256)
    {
        var record = TemplateVersionRecords.Sample(
            templateId,
            version,
            owner: "billing-system",
            packageSha256: packageSha256,
            engine: TemplateEngine.Scriban);
        Versions.Seed(record);
    }

    /// <summary>The store a race needs: the winner is committed by the time this take inserts.</summary>
    public ITemplateVersionStore Racing(string templateId, string version, string winnerSha256) =>
        new RaceOnInsertStore(Versions, TemplateVersionRecords.Sample(templateId, version, owner: "billing-system", packageSha256: winnerSha256));

    public ValueTask DisposeAsync()
    {
        _loggerFactory.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>A store that records the winner of a race the moment this take tries to commit.</summary>
    private sealed class RaceOnInsertStore(InMemoryTemplateVersionStore inner, TemplateVersionRecord winner) : ITemplateVersionStore
    {
        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            inner.FindAsync(templateId, version, cancellationToken);

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            inner.ListByTemplateAsync(templateId, cancellationToken);

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken)
        {
            inner.Seed(winner);
            return inner.TryInsertAsync(record, cancellationToken);
        }
    }
}
