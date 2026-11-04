using ReportService.Testing;
using ReportService.Testing.TemplateSource;

namespace ReportService.TemplateSource.Tests.Listing;

/// <summary>
/// The client's version listing, held to the same contract as the in-memory
/// double, plus the shape of the requests it makes: the walk through
/// <c>templates</c> and the template folder at the commit the caller already
/// resolved, and never a request for one version.
/// </summary>
public sealed class GitHubTemplateSourceListingTests : TemplateVersionListingContract, IDisposable
{
    private readonly GitHubTemplateSourceStub _stub = new();
    private readonly HttpClient _client;

    public GitHubTemplateSourceListingTests() => _client = new HttpClient(_stub);

    /// <inheritdoc />
    protected override ITemplateSource Source => new GitHubTemplateSource(_client, new TemplateSourceOptions
    {
        ApiBaseUrl = GitHubTemplateSourceStub.BaseUrl,
        Repository = GitHubTemplateSourceStub.Owner + "/" + GitHubTemplateSourceStub.Repository,
        Branch = GitHubTemplateSourceStub.Branch,
    });

    /// <inheritdoc />
    protected override void SeedVersionFolder(string templateId, string version) =>
        _stub.SeedVersionFolder(templateId, version);

    /// <inheritdoc />
    protected override void SeedTemplateFolder(string templateId) => _stub.SeedTemplateFolder(templateId);

    /// <inheritdoc />
    protected override void SeedFileInTemplateFolder(string templateId, string name) =>
        _stub.SeedTemplateFile(templateId, name);

    /// <inheritdoc />
    protected override void TruncateTemplateFolder(string templateId) =>
        _stub.SetTemplateFolderTruncated(templateId);

    [Fact]
    public async Task TheListingAsksForTheTreeAtTheResolvedCommitAndNeverForOneVersion()
    {
        SeedVersionFolder("invoice-energy", "1.0.0");
        var source = Source;
        var commit = await source.ResolveBranchAsync(TestContext.Current.CancellationToken);
        var before = _stub.Requests.Count;

        await source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken);

        var listingRequests = _stub.Requests.Skip(before).ToList();
        Assert.NotEmpty(listingRequests);
        Assert.All(listingRequests, request => Assert.StartsWith("/repos/", request.Path, StringComparison.Ordinal));
        Assert.All(listingRequests, request => Assert.Contains("/git/trees/", request.Path, StringComparison.Ordinal));
        Assert.DoesNotContain(listingRequests, request => request.Path.Contains("/commits/", StringComparison.Ordinal));
        Assert.DoesNotContain(listingRequests, request => request.Path.Contains("recursive=1", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
        _stub.Dispose();
    }
}
