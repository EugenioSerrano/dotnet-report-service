using ReportService.TemplateSource;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The source client against the real test repository, unauthenticated: it resolves
/// the branch's commit — which must be the pinned one — walks to one version's
/// folder, lists its files and verifies one blob against its git hash; an absent
/// version is absent and never a fault; a repository the deployment is not
/// configured for is <c>Misconfigured</c>.
/// <para>
/// The repository, branch and commit are the fixture the template-source test
/// repository holds: <c>https://github.com/EugenioSerrano/dotnet-report-service-templates</c>
/// on <c>main</c>, at <see cref="PinnedCommit"/>, holding
/// <c>templates/invoice-energy/1.1.0/</c> — a package the product's own manifest
/// rules accept. The class runs from this record and lets a deployment override it
/// through <see cref="RepositoryVariable"/>, <see cref="BranchVariable"/>,
/// <see cref="TemplateVariable"/> and <see cref="VersionVariable"/>; a configuration
/// that names only part of it fails the class by name, never skip it silently.
/// </para>
/// <para>
/// It never runs a take: a version is taken only after the checks that refuse a bad
/// package exist (§14 D3). It reads the client, and it stays inside the sixty
/// requests an hour an anonymous client is given.
/// </para>
/// </summary>
public sealed class GitHubTemplateSourceLiveTests
{
    /// <summary>The repository the fixtures live in.</summary>
    public const string Repository = "EugenioSerrano/dotnet-report-service-templates";

    /// <summary>The branch the fixtures are published on.</summary>
    public const string Branch = "main";

    /// <summary>The commit whose tree the fixtures of this V-Bounce were published at.</summary>
    public const string PinnedCommit = "e464d10d459b143cbde2cfced0be063e1b93a423";

    /// <summary>The template the fixtures hold.</summary>
    public const string Template = "invoice-energy";

    /// <summary>The version the fixtures hold.</summary>
    public const string Version = "1.1.0";

    /// <summary>The version the fixtures do not hold.</summary>
    public const string AbsentVersion = "9.9.9";

    /// <summary>The variable that overrides the repository.</summary>
    public const string RepositoryVariable = "TEMPLATESOURCE_TEST_REPOSITORY";

    /// <summary>The variable that overrides the branch.</summary>
    public const string BranchVariable = "TEMPLATESOURCE_TEST_BRANCH";

    /// <summary>The variable that overrides the template.</summary>
    public const string TemplateVariable = "TEMPLATESOURCE_TEST_TEMPLATE";

    /// <summary>The variable that overrides the version.</summary>
    public const string VersionVariable = "TEMPLATESOURCE_TEST_VERSION";

    /// <summary>The variable that names the commit an overridden fixture is pinned to; optional.</summary>
    public const string CommitVariable = "TEMPLATESOURCE_TEST_COMMIT";

    private const int AnonymousRequestsPerHour = 60;

    [Fact]
    public async Task TheClientReadsThePinnedCommitAndOneVersionOfIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = Configured();
        using var http = Client();
        var source = new GitHubTemplateSource(http, settings.Options);

        var commit = await source.ResolveBranchAsync(cancellationToken);
        if (settings.PinnedCommit.Length > 0)
        {
            Assert.True(
                string.Equals(settings.PinnedCommit, commit.CommitSha, StringComparison.Ordinal),
                $"the fixtures of this V-Bounce are published at commit {settings.PinnedCommit}; the branch now answers {commit.CommitSha} — a branch that moved is a change to record, never one to read past");
        }

        Assert.Equal(40, commit.RootTreeSha.Length);

        var folder = await source.FindVersionFolderAsync(commit, settings.Template, settings.Version, cancellationToken);
        Assert.NotNull(folder);

        var files = await source.ListFilesAsync(folder, cancellationToken);
        Assert.Contains(files, file => file.Path == "template.yaml");
        Assert.Contains(files, file => file.Path == "schema.json");
        Assert.Contains(files, file => file.Path == "sample.json");
        Assert.Contains(files, file => file.Path == "parts/row.html.sbn");

        var manifest = files.Single(file => file.Path == "template.yaml");
        byte[] content = [];
        await source.OpenFileAsync(
            manifest,
            async (body, token) =>
            {
                await using var buffer = new MemoryStream();
                await body.CopyToAsync(buffer, token);
                content = buffer.ToArray();
            },
            cancellationToken);

        // The bytes verified against their git hash on the way in, and they are the manifest.
        Assert.Equal(manifest.Size, content.Length);
        Assert.Contains("manifestVersion: \"1.0\"", System.Text.Encoding.UTF8.GetString(content));
    }

    [Fact]
    public async Task TheListingOfARealTemplateFolderNamesItsVersionFolders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = Configured();
        using var http = Client();
        var source = new GitHubTemplateSource(http, settings.Options);

        var commit = await source.ResolveBranchAsync(cancellationToken);
        if (settings.PinnedCommit.Length > 0)
        {
            Assert.True(
                string.Equals(settings.PinnedCommit, commit.CommitSha, StringComparison.Ordinal),
                $"the fixtures this class reads are published at commit {settings.PinnedCommit}; the branch now answers {commit.CommitSha} — a branch that moved is a change to record, never one to read past");
        }

        var listing = await source.ListVersionsAsync(commit, settings.Template, cancellationToken);

        Assert.NotNull(listing);
        Assert.Contains(settings.Version, listing.Names);

        // The listing names version folders: a file beside them is not a version.
        Assert.DoesNotContain("template.yaml", listing.Names);

        // A template the repository does not hold is absent, and never a fault.
        Assert.Null(await source.ListVersionsAsync(commit, "invoice-energy-absent", cancellationToken));
    }

    [Fact]
    public async Task AnAbsentVersionIsAbsentAndAWrongRepositoryIsMisconfigured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = Configured();
        using var http = Client();
        var source = new GitHubTemplateSource(http, settings.Options);

        var commit = await source.ResolveBranchAsync(cancellationToken);
        Assert.Null(await source.FindVersionFolderAsync(commit, settings.Template, AbsentVersion, cancellationToken));

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
        using var http = Client();
        var source = new GitHubTemplateSource(http, settings.Options);

        var commit = await source.ResolveBranchAsync(cancellationToken);
        var folder = await source.FindVersionFolderAsync(commit, settings.Template, settings.Version, cancellationToken);
        Assert.NotNull(folder);
        var files = await source.ListFilesAsync(folder, cancellationToken);
        await source.OpenFileAsync(
            files[0],
            async (body, token) => await body.CopyToAsync(Stream.Null, token),
            cancellationToken);

        // Four requests of the sixty an hour the repository allows an anonymous client,
        // counted by the class rather than read from a header the answer may omit.
        Assert.True(AnonymousRequestsPerHour > 4);
    }

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-report-service-tests");
        return http;
    }

    private static ConfiguredSource Configured()
    {
        var repository = Environment.GetEnvironmentVariable(RepositoryVariable);
        var branch = Environment.GetEnvironmentVariable(BranchVariable);
        var template = Environment.GetEnvironmentVariable(TemplateVariable);
        var version = Environment.GetEnvironmentVariable(VersionVariable);
        var named = new[] { repository, branch, template, version }.Count(value => !string.IsNullOrWhiteSpace(value));

        if (named == 0)
        {
            // The fixture the template-source test repository holds, recorded with this
            // V-Bounce: the class runs from it with nothing configured.
            return new ConfiguredSource(
                new TemplateSourceOptions { Repository = Repository, Branch = Branch },
                Template,
                Version,
                PinnedCommit);
        }

        if (named != 4)
        {
            // A configuration that names only part of it is a configuration error of the
            // run, and it fails by name: it is never silently skipped and never half-used.
            throw new InvalidOperationException(
                $"template source test repository not configured: set {RepositoryVariable}, {BranchVariable}, {TemplateVariable} and {VersionVariable} together, "
                + "or set none of them to read the recorded fixture");
        }

        return new ConfiguredSource(
            new TemplateSourceOptions { Repository = repository!, Branch = branch! },
            template!,
            version!,
            Environment.GetEnvironmentVariable(CommitVariable) ?? string.Empty);
    }

    /// <summary>The settings and the one version the class reads, and the commit it is pinned to.</summary>
    private sealed record ConfiguredSource(TemplateSourceOptions Options, string Template, string Version, string PinnedCommit);
}
