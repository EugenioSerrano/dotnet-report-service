using Microsoft.Extensions.Logging.Abstractions;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Checks;
using ReportService.TemplateSource.Resolution;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Workers.Pdf.Templates;
using Temporalio.Exceptions;

namespace ReportService.Workers.Pdf.Tests.Templates;

/// <summary>
/// The by-name resolution as the queue serves it: one activity that runs the
/// resolution through the seam over the take, answers the record's identifying
/// values the caller pins, and turns a refusal into the engine's failure with
/// that refusal's own code, text and retryable flag. The reason a job would show
/// is capped at one kilobyte, and a failure of the store is never turned into a
/// template's refusal.
/// </summary>
public sealed class ResolveTemplateVersionActivityTests : IDisposable
{
    private const string Consumer = "billing-system";
    private const string Template = "invoice-energy";
    private const string Version = "1.1.0";

    private readonly InMemoryTemplateSource _source = new();
    private readonly InMemoryTemplateVersionStore _store = new();
    private readonly InMemoryTemplateTake _take = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly TemplateByNameMetrics _metrics = new();

    public void Dispose() => _metrics.Dispose();

    [Fact]
    public async Task AResolutionIsAnsweredWithTheRecordsIdentifyingValues()
    {
        SeedSource("1.0.0", Version);
        _store.Seed(TemplateVersionRecords.Sample(Template, "1.0.0"));
        var record = TemplateVersionRecords.Sample(Template, Version, packageSha256: new string('d', 64));
        _take.SeedTaken(Template, Version, record);

        var result = await Resolve(Consumer);

        Assert.Equal(ResolveTemplateVersionOutcomes.Resolved, result.Outcome);
        Assert.Equal(Template, result.TemplateId);
        Assert.Equal(Version, result.Version);
        Assert.Equal(Consumer, result.OwnerConsumerId);
        Assert.False(result.Shared);
        Assert.Equal(record.CommitSha, result.CommitSha);
        Assert.Equal(record.PackageSha256, result.PackageSha256);
        Assert.Equal(record.PackageSize, result.PackageSize);
        Assert.Equal(TemplateEngineNames.ToStoredText(record.Engine), result.Engine);
        Assert.Equal(record.EngineVersion, result.EngineVersion);
        Assert.Equal(record.SchemaRef, result.SchemaRef);

        // The result carries no manifest and no path: the caller pins identifiers only.
        Assert.DoesNotContain(record.ObjectKey.ToString(), result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("manifest", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AVersionUsedWhileTheSourceCouldNotBeCheckedIsAnsweredAsFromTheTakenCopy()
    {
        _source.SetUnreachable(true);
        _store.Seed(TemplateVersionRecords.Sample(Template, "1.0.0"));

        var result = await Resolve(Consumer);

        Assert.Equal(ResolveTemplateVersionOutcomes.ResolvedFromTakenCopy, result.Outcome);
        Assert.Equal("1.0.0", result.Version);
        Assert.Empty(_take.Calls);
    }

    [Fact]
    public async Task ARefusalIsTheEnginesFailureWithItsOwnCodeAndFlag()
    {
        SeedSource("1.0.0", Version);
        _take.SeedRefused(Template, Version, TemplateCheckCodes.TemplateInvalid, "The folder does not match the version the manifest declares.", retryable: false);
        _take.SeedRefused(Template, "1.0.0", TemplateCheckCodes.TemplateInvalid, "The folder does not match the version the manifest declares.", retryable: false);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Resolve(Consumer));

        Assert.Equal(TemplateResolutionCodes.TemplateInvalid, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains(Version, failure.Message, StringComparison.Ordinal);
        Assert.Contains("does not match the version", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourceThatCouldNotBeCheckedWithNothingTakenIsRetryable()
    {
        _source.SetUnreachable(true);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Resolve(Consumer));

        Assert.Equal(TemplateResolutionCodes.TemplateSourceUnavailable, failure.ErrorType);
        Assert.False(failure.NonRetryable);
        Assert.Equal(TemplateVersionResolver.SourceUnavailableReason, failure.Message);
    }

    [Fact]
    public async Task TheReasonAJobWouldShowIsCappedAtOneKilobyte()
    {
        SeedSource("1.0.0");
        _take.SeedRefused(Template, "1.0.0", TemplateCheckCodes.TemplateInvalid, new string('x', 4_000), retryable: false);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => Resolve(Consumer));

        Assert.Equal(ResolveTemplateVersionActivity.ReasonLimit, failure.Message.Length);
        Assert.EndsWith("...", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoreFailureIsNeverTurnedIntoTheTemplatesRefusal()
    {
        SeedSource("1.0.0");
        var store = new UnavailableStore();

        var failure = await Assert.ThrowsAsync<TemplateStoreUnavailableException>(() => Resolve(Consumer, store));

        Assert.IsType<TimeoutException>(failure.InnerException);
    }

    private Task<ResolveTemplateVersionResult> Resolve(string consumer, ITemplateVersionStore? store = null)
    {
        var resolver = new TemplateVersionResolver(
            _source,
            store ?? _store,
            _take,
            _metrics,
            _time,
            NullLogger<TemplateVersionResolver>.Instance,
            TimeSpan.FromSeconds(240));
        var activity = new ResolveTemplateVersionActivity(resolver);

        return new Temporalio.Testing.ActivityEnvironment().RunAsync(
            () => activity.RunAsync(new ResolveTemplateVersionInput(Template, consumer, "job-0001")));
    }

    private void SeedSource(params string[] versions)
    {
        foreach (var version in versions)
        {
            _source.SeedPackage(Template, version, new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["template.yaml"] = System.Text.Encoding.UTF8.GetBytes(
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
                    """),
                ["schema.json"] = System.Text.Encoding.UTF8.GetBytes("{}"),
                ["html/row.html.sbn"] = System.Text.Encoding.UTF8.GetBytes("<tr></tr>"),
            });
        }
    }

    /// <summary>A store that does not answer, as a database that went away mid-resolution.</summary>
    private sealed class UnavailableStore : ITemplateVersionStore
    {
        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            Task.FromException<TemplateVersionRecord?>(new TemplateStoreUnavailableException(new TimeoutException("the store did not answer")));

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<TemplateVersionRecord>>(new TemplateStoreUnavailableException(new TimeoutException("the store did not answer")));

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) =>
            Task.FromException<TemplateVersionInsert>(new TemplateStoreUnavailableException(new TimeoutException("the store did not answer")));
    }
}
