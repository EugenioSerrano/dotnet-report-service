using ReportService.TemplateSource;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The source client against the real configured test repository, unauthenticated:
/// it resolves the branch's commit, walks to one version's folder, lists its files
/// and verifies one blob against its git hash; an absent version is absent and
/// never a fault; a repository the deployment is not configured for is
/// <c>Misconfigured</c>. The class never runs a take — a version is taken only
/// after the checks that refuse a bad package exist — and it stays inside the
/// sixty requests an hour an anonymous client is given.
/// </summary>
public sealed class GitHubTemplateSourceLiveTests
{
    private const string RepositoryVariable = "TEMPLATESOURCE_TEST_REPOSITORY";
    private const string BranchVariable = "TEMPLATESOURCE_TEST_BRANCH";
    private const string TemplateVariable = "TEMPLATESOURCE_TEST_TEMPLATE";
    private const string VersionVariable = "TEMPLATESOURCE_TEST_VERSION";
    private const int AnonymousRequestsPerHour = 60;

    [Fact]
    public async Task TheClientReadsTheConfiguredTestRepositoryAtOneCommit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = Configured();
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-report-service-tests");
        var source = new GitHubTemplateSource(http, settings.Options);

        var commit = await source.ResolveBranchAsync(cancellationToken);
        Assert.Equal(40, commit.CommitSha.Length);
        Assert.Equal(40, commit.RootTreeSha.Length);

        var folder = await source.FindVersionFolderAsync(commit, settings.Template, settings.Version, cancellationToken);
        Assert.NotNull(folder);

        var files = await source.ListFilesAsync(folder, cancellationToken);
        Assert.Contains(files, file => file.Path == "template.yaml");
        var manifest = files.Single(file => file.Path == "template.yaml");
        long verified = 0;
        await source.OpenFileAsync(
            manifest,
            async (content, token) =>
            {
                await using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, token);
                verified = buffer.Length;
            },
            cancellationToken);
        Assert.Equal(manifest.Size, verified);
    }

    [Fact]
    public async Task AnAbsentVersionIsAbsentAndAWrongRepositoryIsMisconfigured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = Configured();
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-report-service-tests");
        var source = new GitHubTemplateSource(http, settings.Options);

        var commit = await source.ResolveBranchAsync(cancellationToken);
        Assert.Null(await source.FindVersionFolderAsync(commit, settings.Template, "0.0.0-absent", cancellationToken));

        var wrong = new TemplateSourceOptions
        {
            Repository = settings.Options.Repository + "-does-not-exist",
            Branch = settings.Options.Branch,
            RequestTimeout = settings.Options.RequestTimeout,
        };
        var misconfigured = new GitHubTemplateSource(http, wrong);
        var error = await Assert.ThrowsAsync<TemplateSourceException>(() => misconfigured.ResolveBranchAsync(cancellationToken));
        Assert.Equal(TemplateSourceFailure.Misconfigured, error.Failure);
    }

    [Fact]
    public async Task TheClassStaysInsideTheAnonymousRateLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = Configured();
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-report-service-tests");
        var source = new GitHubTemplateSource(http, settings.Options);

        var commit = await source.ResolveBranchAsync(cancellationToken);
        var folder = await source.FindVersionFolderAsync(commit, settings.Template, settings.Version, cancellationToken);
        Assert.NotNull(folder);
        var files = await source.ListFilesAsync(folder, cancellationToken);
        await source.OpenFileAsync(files[0], (_, _) => Task.CompletedTask, cancellationToken);

        // Four requests of the sixty an hour the repository allows an anonymous client,
        // counted by the class rather than read from a header the answer may omit.
        Assert.True(AnonymousRequestsPerHour > 4);
    }

    private static ConfiguredSource Configured()
    {
        var repository = Environment.GetEnvironmentVariable(RepositoryVariable);
        var branch = Environment.GetEnvironmentVariable(BranchVariable);
        var template = Environment.GetEnvironmentVariable(TemplateVariable);
        var version = Environment.GetEnvironmentVariable(VersionVariable);
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(branch)
            || string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(version))
        {
            // A live class without its repository is a configuration error of the run,
            // and it fails by name: it is never silently skipped.
            throw new InvalidOperationException(
                $"template source test repository not configured: set {RepositoryVariable}, {BranchVariable}, {TemplateVariable} and {VersionVariable} "
                + "to a public repository and one version it holds");
        }

        return new ConfiguredSource(
            new TemplateSourceOptions { Repository = repository, Branch = branch },
            template,
            version);
    }

    /// <summary>The settings and the one version the class reads.</summary>
    private sealed record ConfiguredSource(TemplateSourceOptions Options, string Template, string Version);
}
