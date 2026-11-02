using System.Net;
using System.Text;
using ReportService.Testing.TemplateSource;

namespace ReportService.TemplateSource.Tests;

/// <summary>
/// The source client against the source's documented shapes: one commit for a
/// whole read, the four requests shaped by the settings, a blob verified against
/// its git hash, and every failure classified as unreachable, misconfigured or
/// too large — never as "does not exist".
/// </summary>
public sealed class GitHubTemplateSourceTests
{
    private const string PackageJson = "{\"manifestVersion\":\"1.0\"}";

    [Fact]
    public async Task TheBranchIsResolvedToOneCommitAndItsRootTree()
    {
        using var client = Client(stub: out var stub);

        var commit = await Source(client).ResolveBranchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(stub.CommitSha, commit.CommitSha);
        Assert.Equal(stub.RootTreeSha, commit.RootTreeSha);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("/repos/" + GitHubTemplateSourceStub.Owner + "/" + GitHubTemplateSourceStub.Repository + "/commits/main", request.Path);
        Assert.StartsWith("dotnet-report-service/", request.UserAgent, StringComparison.Ordinal);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Equal(TemplateSourceOptions.ApiVersion, request.ApiVersion);
        Assert.Empty(request.Authorization);
    }

    [Fact]
    public async Task TheTokenIsDeclaredAsABearerCredentialAndOnlyToTheSource()
    {
        using var client = Client(stub: out var stub, token: "ghp_test");

        await Source(client, token: "ghp_test").ResolveBranchAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Bearer ghp_test", Assert.Single(stub.Requests).Authorization);
    }

    [Fact]
    public async Task TheVersionFolderIsFoundByWalkingTheTreeNames()
    {
        using var client = Client(stub: out var stub);
        stub.SeedPackage("invoice-energy", "1.1.0", new Dictionary<string, byte[]> { ["template.yaml"] = Bytes(PackageJson) });
        var source = Source(client);

        var commit = await source.ResolveBranchAsync(TestContext.Current.CancellationToken);
        var folder = await source.FindVersionFolderAsync(commit, "invoice-energy", "1.1.0", TestContext.Current.CancellationToken);

        Assert.True(
            folder is not null,
            $"commit {commit.CommitSha} tree {commit.RootTreeSha} stub root {stub.RootTreeSha}: "
            + string.Join(" | ", stub.Requests.Select(request => request.Path)));
        Assert.Null(await source.FindVersionFolderAsync(commit, "invoice-energy", "9.9.9", TestContext.Current.CancellationToken));
        Assert.Null(await source.FindVersionFolderAsync(commit, "no-such-template", "1.1.0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheFilesOfTheFolderAreListedWithTheirGitNamesAndSizes()
    {
        using var client = Client(stub: out var stub);
        var manifest = Bytes(PackageJson);
        stub.SeedPackage("invoice-energy", "1.1.0", new Dictionary<string, byte[]>
        {
            ["template.yaml"] = manifest,
            ["html/row.html.sbn"] = Bytes("row"),
        });
        var source = Source(client);

        var commit = await source.ResolveBranchAsync(TestContext.Current.CancellationToken);
        var folder = await source.FindVersionFolderAsync(commit, "invoice-energy", "1.1.0", TestContext.Current.CancellationToken);
        var files = await source.ListFilesAsync(folder!, TestContext.Current.CancellationToken);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.Path == "template.yaml" && file.BlobSha == GitHubTemplateSourceStub.BlobSha(manifest) && file.Size == manifest.Length);
        Assert.Contains(files, file => file.Path == "html/row.html.sbn");
        Assert.EndsWith("/git/trees/" + folder!.TreeSha + "?recursive=1", stub.Requests[^1].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneCommitIsUsedForTheWholeReadEvenWhenTheBranchLaterMoves()
    {
        using var client = Client(stub: out var stub);
        var original = Bytes(PackageJson);
        stub.SeedPackage("invoice-energy", "1.1.0", new Dictionary<string, byte[]> { ["template.yaml"] = original });
        var source = Source(client);
        var before = await source.ResolveBranchAsync(TestContext.Current.CancellationToken);
        var folder = await source.FindVersionFolderAsync(before, "invoice-energy", "1.1.0", TestContext.Current.CancellationToken);
        var file = Assert.Single(await source.ListFilesAsync(folder!, TestContext.Current.CancellationToken));

        stub.ReplacePackage("invoice-energy", "1.1.0", new Dictionary<string, byte[]> { ["template.yaml"] = Bytes("{\"manifestVersion\":\"9.9\"}") });
        Assert.NotEqual(before.CommitSha, stub.CommitSha);

        using var received = new MemoryStream();
        await source.OpenFileAsync(file, (stream, token) => stream.CopyToAsync(received, token), TestContext.Current.CancellationToken);

        Assert.Equal(original, received.ToArray());
        Assert.Single(stub.Requests, request => request.Path.Contains("/commits/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileIsDeliveredAndItsGitHashIsVerifiedAsItStreams()
    {
        using var client = Client(stub: out var stub);
        var content = Bytes("a row of the package");
        stub.SeedPackage("invoice-energy", "1.1.0", new Dictionary<string, byte[]> { ["row.html.sbn"] = content });
        var source = Source(client);
        var file = await OnlyFileAsync(source, stub);

        using var received = new MemoryStream();
        await source.OpenFileAsync(file, async (stream, token) => await stream.CopyToAsync(received, token), TestContext.Current.CancellationToken);

        Assert.Equal(content, received.ToArray());
        Assert.Equal("application/vnd.github.raw+json", stub.Requests[^1].Accept);
    }

    [Fact]
    public async Task BytesThatDoNotMatchTheDeclaredHashAreReportedUnreachable()
    {
        using var client = Client(stub: out var stub);
        var content = Bytes("a row of the package");
        stub.SeedPackage("invoice-energy", "1.1.0", new Dictionary<string, byte[]> { ["row.html.sbn"] = content });
        var source = Source(client);
        var file = await OnlyFileAsync(source, stub);
        stub.OverrideBlob(file.BlobSha, Bytes("another row entirely"));

        var failure = await Assert.ThrowsAsync<TemplateSourceException>(
            () => source.OpenFileAsync(file, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken));

        Assert.Equal(TemplateSourceFailure.Unreachable, failure.Failure);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public async Task AFileThatDeliversFewerBytesThanDeclaredIsReportedUnreachable()
    {
        using var client = Client(stub: out var stub);
        var content = Bytes("a row of the package");
        stub.SeedPackage("invoice-energy", "1.1.0", new Dictionary<string, byte[]> { ["row.html.sbn"] = content });
        var source = Source(client);
        var file = await OnlyFileAsync(source, stub);
        stub.OverrideBlob(file.BlobSha, content[..4]);

        var failure = await Assert.ThrowsAsync<TemplateSourceException>(
            () => source.OpenFileAsync(file, (stream, token) => stream.CopyToAsync(Stream.Null, token), TestContext.Current.CancellationToken));

        Assert.Equal(TemplateSourceFailure.Unreachable, failure.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false, TemplateSourceFailure.Unreachable)]
    [InlineData(HttpStatusCode.TooManyRequests, true, TemplateSourceFailure.Unreachable)]
    [InlineData(HttpStatusCode.Forbidden, true, TemplateSourceFailure.Unreachable)]
    [InlineData(HttpStatusCode.Unauthorized, false, TemplateSourceFailure.Misconfigured)]
    [InlineData(HttpStatusCode.NotFound, false, TemplateSourceFailure.Misconfigured)]
    [InlineData(HttpStatusCode.Conflict, false, TemplateSourceFailure.Misconfigured)]
    public async Task EveryAnswerTheSourceCanGiveIsClassified(HttpStatusCode status, bool rateLimited, TemplateSourceFailure expected)
    {
        using var client = Client(stub: out var stub);
        stub.SetStatus(status, rateLimited);

        var failure = await Assert.ThrowsAsync<TemplateSourceException>(
            () => Source(client).ResolveBranchAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected, failure.Failure);
        Assert.DoesNotContain("does not exist", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASourceThatAnswersNothingIsReportedUnreachableAndRetryable()
    {
        using var client = Client(stub: out var stub, token: "ghp_planted");
        stub.Stop();

        var failure = await Assert.ThrowsAsync<TemplateSourceException>(
            () => Source(client, token: "ghp_planted").ResolveBranchAsync(TestContext.Current.CancellationToken));

        Assert.Equal(TemplateSourceFailure.Unreachable, failure.Failure);
        Assert.True(failure.Retryable);
        Assert.DoesNotContain("ghp_planted", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATreeTooLargeToAnswerInOnePageIsRefusedRatherThanReadAsAbsent()
    {
        using var client = Client(stub: out var stub);
        stub.SeedPackage("invoice-energy", "1.1.0", new Dictionary<string, byte[]> { ["template.yaml"] = Bytes(PackageJson) });
        stub.SetTruncated(true);
        var source = Source(client);
        var commit = await source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<TemplateSourceException>(
            () => source.FindVersionFolderAsync(commit, "invoice-energy", "1.1.0", TestContext.Current.CancellationToken));

        Assert.Equal(TemplateSourceFailure.PackageTooLarge, failure.Failure);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public async Task ASourceThatAnswersNoTemplateFolderAtAllIsAbsentAndNotAnError()
    {
        using var client = Client(stub: out _);
        var source = Source(client);
        var commit = await source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        Assert.Null(await source.FindVersionFolderAsync(commit, "invoice-energy", "1.1.0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSettingsAreCheckedBeforeAnyRequestIsMade()
    {
        using var client = Client(stub: out var stub);
        var invalid = new TemplateSourceOptions { Repository = "not a repository", Branch = "main" };

        Assert.Throws<ArgumentException>(() => new GitHubTemplateSource(client, invalid));
        Assert.Equal(0, stub.RequestCount);
    }

    private static HttpClient Client(out GitHubTemplateSourceStub stub, string? token = null)
    {
        stub = new GitHubTemplateSourceStub();
        return new HttpClient(stub);
    }

    private static GitHubTemplateSource Source(HttpClient client, string? token = null) => new(client, new TemplateSourceOptions
    {
        ApiBaseUrl = GitHubTemplateSourceStub.BaseUrl,
        Repository = GitHubTemplateSourceStub.Owner + "/" + GitHubTemplateSourceStub.Repository,
        Branch = GitHubTemplateSourceStub.Branch,
        Token = token,
    });

    private static async Task<SourceFile> OnlyFileAsync(GitHubTemplateSource source, GitHubTemplateSourceStub stub)
    {
        var commit = await source.ResolveBranchAsync(TestContext.Current.CancellationToken);
        var folder = await source.FindVersionFolderAsync(commit, "invoice-energy", "1.1.0", TestContext.Current.CancellationToken);
        var files = await source.ListFilesAsync(folder!, TestContext.Current.CancellationToken);
        return Assert.Single(files);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
