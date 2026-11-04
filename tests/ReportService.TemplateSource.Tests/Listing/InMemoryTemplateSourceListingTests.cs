using ReportService.Testing;
using ReportService.Testing.TemplateSource;

namespace ReportService.TemplateSource.Tests.Listing;

/// <summary>
/// The in-memory source's version listing, held to the same contract as the
/// client, plus what only the double can show: one step per listing, the step
/// naming the template and no version, and the branch edits a case makes — a
/// removed version or an absent template.
/// </summary>
public sealed class InMemoryTemplateSourceListingTests : TemplateVersionListingContract
{
    private readonly InMemoryTemplateSource _source = new();

    /// <inheritdoc />
    protected override ITemplateSource Source => _source;

    /// <inheritdoc />
    protected override void SeedVersionFolder(string templateId, string version) =>
        _source.SeedVersionFolder(templateId, version);

    /// <inheritdoc />
    protected override void SeedTemplateFolder(string templateId) => _source.SeedTemplateFolder(templateId);

    /// <inheritdoc />
    protected override void SeedFileInTemplateFolder(string templateId, string name) =>
        _source.SeedFileInTemplateFolder(templateId, name);

    /// <inheritdoc />
    protected override void TruncateTemplateFolder(string templateId) =>
        _source.SetListingTooLarge(templateId);

    [Fact]
    public async Task OneListingIsOneRequestThatNamesTheTemplateAndNoVersion()
    {
        SeedVersionFolder("invoice-energy", "1.0.0");
        var commit = await _source.ResolveBranchAsync(TestContext.Current.CancellationToken);
        var before = _source.RequestCount;

        await _source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken);

        Assert.Equal(before + 1, _source.RequestCount);
        var request = _source.RequestLog[^1];
        Assert.Equal("tree", request.Step);
        Assert.Equal("invoice-energy", request.TemplateId);
        Assert.Null(request.Version);
    }

    [Fact]
    public async Task RemovingAVersionTakesItOutOfTheListing()
    {
        SeedVersionFolder("invoice-energy", "1.0.0");
        SeedVersionFolder("invoice-energy", "1.1.0");
        _source.Remove("invoice-energy", "1.1.0");
        var commit = await _source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var versions = await _source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken);

        Assert.NotNull(versions);
        Assert.Equal(["1.0.0"], versions.Names);
    }

    [Fact]
    public async Task AnAbsentTemplateAnswersAbsentEvenWhenItsVersionsWereSeeded()
    {
        SeedVersionFolder("invoice-energy", "1.0.0");
        _source.SetTemplateAbsent("invoice-energy");
        var commit = await _source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        Assert.Null(await _source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken));
        Assert.Null(await _source.FindVersionFolderAsync(commit, "invoice-energy", "1.0.0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnreachableSourceRefusesTheListing()
    {
        SeedVersionFolder("invoice-energy", "1.0.0");
        var commit = await _source.ResolveBranchAsync(TestContext.Current.CancellationToken);
        _source.SetUnreachable(true);

        var exception = await Assert.ThrowsAsync<TemplateSourceException>(
            () => _source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken));

        Assert.Equal(TemplateSourceFailure.Unreachable, exception.Failure);
    }
}
