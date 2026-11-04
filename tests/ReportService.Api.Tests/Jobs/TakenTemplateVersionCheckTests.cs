using ReportService.Api.Jobs;
using ReportService.Testing;
using ReportService.TemplatePackages.Records;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The taken versions a request is judged against: an exact version this consumer may use
/// is accepted with no baseline, one another consumer owns and does not share is refused
/// exactly like one that does not exist, and a reference by name leaves with the highest
/// usable release version as its baseline - never a pre-release, never another consumer's
/// private version, and never a version whose precedence ties without a rule.
/// </summary>
public sealed class TakenTemplateVersionCheckTests
{
    private const string Consumer = "consumer-a";

    private const string OtherConsumer = "consumer-b";

    private readonly InMemoryTemplateVersionStore _versions = new();

    private readonly TakenTemplateVersionCheck _check;

    public TakenTemplateVersionCheckTests() => _check = new TakenTemplateVersionCheck(_versions);

    [Fact]
    public async Task AnExactVersionThatWasNeverTakenIsAcceptedWithoutABaseline()
    {
        var answer = await CheckAsync("invoice-energy", "1.1.0");

        Assert.True(answer.Usable);
        Assert.Null(answer.BaselineVersion);
        Assert.Equal(1, _versions.FindCalls);
        Assert.Equal(0, _versions.ListCalls);
    }

    [Fact]
    public async Task AnExactVersionThisConsumerOwnsIsAccepted()
    {
        _versions.Seed(TemplateVersionRecords.Sample(version: "1.1.0", owner: Consumer));

        var answer = await CheckAsync("invoice-energy", "1.1.0");

        Assert.True(answer.Usable);
        Assert.Null(answer.BaselineVersion);
    }

    [Fact]
    public async Task AnExactVersionOfAnotherConsumerThatIsSharedIsAccepted()
    {
        _versions.Seed(TemplateVersionRecords.Sample(version: "1.1.0", owner: OtherConsumer, shared: true));

        var answer = await CheckAsync("invoice-energy", "1.1.0");

        Assert.True(answer.Usable);
    }

    [Fact]
    public async Task AnExactVersionOfAnotherConsumerThatIsNotSharedIsRefused()
    {
        _versions.Seed(TemplateVersionRecords.Sample(version: "1.1.0", owner: OtherConsumer, shared: false));

        var answer = await CheckAsync("invoice-energy", "1.1.0");

        Assert.False(answer.Usable);
        Assert.Null(answer.BaselineVersion);
    }

    [Fact]
    public async Task AReferenceByNameWithNoTakenVersionHasNoBaseline()
    {
        var answer = await CheckAsync("invoice-energy", null);

        Assert.True(answer.Usable);
        Assert.Null(answer.BaselineVersion);
        Assert.Equal(1, _versions.ListCalls);
        Assert.Equal(0, _versions.FindCalls);
    }

    [Fact]
    public async Task OnlyPreReleasesTakenLeaveAReferenceByNameWithoutABaseline()
    {
        _versions.Seed(TemplateVersionRecords.Sample(version: "2.0.0-beta.1", owner: Consumer));

        var answer = await CheckAsync("invoice-energy", null);

        Assert.True(answer.Usable);
        Assert.Null(answer.BaselineVersion);
    }

    [Fact]
    public async Task OnlyAnotherConsumersPrivateVersionsTakenLeaveAReferenceByNameWithoutABaseline()
    {
        _versions.Seed(TemplateVersionRecords.Sample(version: "1.1.0", owner: OtherConsumer, shared: false));

        var answer = await CheckAsync("invoice-energy", null);

        Assert.True(answer.Usable);
        Assert.Null(answer.BaselineVersion);
    }

    [Fact]
    public async Task TheBaselineIsTheHighestUsableReleaseAndNeverAPreRelease()
    {
        _versions.Seed(TemplateVersionRecords.Sample(version: "1.0.0", owner: Consumer));
        _versions.Seed(TemplateVersionRecords.Sample(version: "1.1.0", owner: Consumer));
        _versions.Seed(TemplateVersionRecords.Sample(version: "1.10.0", owner: OtherConsumer, shared: true));
        _versions.Seed(TemplateVersionRecords.Sample(version: "2.0.0-beta.1", owner: Consumer));

        var answer = await CheckAsync("invoice-energy", null);

        Assert.Equal("1.10.0", answer.BaselineVersion);
    }

    [Fact]
    public async Task TwoVersionsOfEqualPrecedenceAreOrderedByTheirFullText()
    {
        _versions.Seed(TemplateVersionRecords.Sample(version: "1.0.0", owner: Consumer));
        _versions.Seed(TemplateVersionRecords.Sample(version: "1.0.0+build.5", owner: Consumer));

        var answer = await CheckAsync("invoice-energy", null);

        Assert.Equal("1.0.0+build.5", answer.BaselineVersion);
    }

    [Fact]
    public async Task AVersionThatIsNotASemanticVersionIsNeverABaseline()
    {
        _versions.Seed(TemplateVersionRecords.Sample(version: "not-a-version", owner: Consumer));
        _versions.Seed(TemplateVersionRecords.Sample(version: "1.2.0", owner: Consumer));

        var answer = await CheckAsync("invoice-energy", null);

        Assert.Equal("1.2.0", answer.BaselineVersion);
    }

    [Fact]
    public async Task TheStoreThatCannotBeReadIsReportedAsSuch()
    {
        var check = new TakenTemplateVersionCheck(new UnavailableStore());

        await Assert.ThrowsAsync<TemplateStoreUnavailableException>(() => check.CheckAsync("invoice-energy", null, Consumer, TestContext.Current.CancellationToken));
    }

    private Task<TakenTemplateVersionCheck.Answer> CheckAsync(string templateId, string? version) =>
        _check.CheckAsync(templateId, version, Consumer, TestContext.Current.CancellationToken);

    /// <summary>A taken-version store no reader can reach.</summary>
    private sealed class UnavailableStore : ITemplateVersionStore
    {
        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken) =>
            throw new TemplateStoreUnavailableException(new TimeoutException("the store did not answer"));

        public Task<IReadOnlyList<TemplateVersionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken) =>
            throw new TemplateStoreUnavailableException(new TimeoutException("the store did not answer"));

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) =>
            throw new TemplateStoreUnavailableException(new TimeoutException("the store did not answer"));
    }
}
