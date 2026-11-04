using ReportService.TemplateSource;

namespace ReportService.TemplateSource.Tests;

/// <summary>
/// The settings of the template source: every bound is checked, the section has
/// no default repository, and a problem never carries the value it refuses — in
/// particular never the token.
/// </summary>
public sealed class TemplateSourceOptionsTests
{
    private const string PlantedToken = "ghp_plantedTokenValueNeverLogged1234567890";

    [Fact]
    public void TheSettingsAProcessNeedsAreValid()
    {
        Assert.Empty(Valid().Validate());
    }

    [Fact]
    public void TheSectionHasNoDefaultRepositoryOrBranch()
    {
        var problems = new TemplateSourceOptions().Validate().ToList();

        Assert.Contains(problems, problem => problem.Setting == nameof(TemplateSourceOptions.Repository));
        Assert.Contains(problems, problem => problem.Setting == nameof(TemplateSourceOptions.Branch));
    }

    [Theory]
    [InlineData("http://api.github.com/")]
    [InlineData("ftp://api.github.com/")]
    [InlineData("not a uri")]
    [InlineData("")]
    public void TheApiBaseUrlMustBeHttpsOrAnAdmittedLoopback(string apiBaseUrl)
    {
        var options = Valid();
        options.ApiBaseUrl = apiBaseUrl;

        Assert.Contains(options.Validate(), problem => problem.Setting == nameof(TemplateSourceOptions.ApiBaseUrl));
    }

    [Fact]
    public void APlainTextLoopbackIsAdmittedOnlyWhenTheCallerAdmitsIt()
    {
        var options = Valid();
        options.ApiBaseUrl = "http://127.0.0.1:5000/";

        Assert.Contains(options.Validate(), problem => problem.Setting == nameof(TemplateSourceOptions.ApiBaseUrl));

        options.AllowInsecureLoopback = true;
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("owner/name/extra")]
    [InlineData("/name")]
    [InlineData("own er/name")]
    [InlineData("")]
    public void TheRepositoryMustBeOwnerAndName(string repository)
    {
        var options = Valid();
        options.Repository = repository;

        Assert.Contains(options.Validate(), problem => problem.Setting == nameof(TemplateSourceOptions.Repository));
    }

    [Theory]
    [InlineData("feature/version-taking")]
    [InlineData("main")]
    [InlineData("release_1.0")]
    public void ABranchNameOfSegmentsIsAccepted(string branch)
    {
        var options = Valid();
        options.Branch = branch;

        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("../main")]
    [InlineData("/main")]
    [InlineData("main/")]
    [InlineData("main branch")]
    [InlineData("")]
    public void ABranchNameThatCouldEscapeOrIsNotSimpleIsRefused(string branch)
    {
        var options = Valid();
        options.Branch = branch;

        Assert.Contains(options.Validate(), problem => problem.Setting == nameof(TemplateSourceOptions.Branch));
    }

    [Fact]
    public void EveryBoundIsChecked()
    {
        AssertRefused(options => options.RequestTimeout = TimeSpan.FromSeconds(0), nameof(TemplateSourceOptions.RequestTimeout));
        AssertRefused(options => options.RequestTimeout = TimeSpan.FromSeconds(61), nameof(TemplateSourceOptions.RequestTimeout));
        AssertRefused(options => options.ReadDeadline = TimeSpan.FromSeconds(4), nameof(TemplateSourceOptions.ReadDeadline));
        AssertRefused(options => options.ReadDeadline = TimeSpan.FromSeconds(121), nameof(TemplateSourceOptions.ReadDeadline));
        AssertRefused(options => options.ResolveDeadline = TimeSpan.FromSeconds(4), nameof(TemplateSourceOptions.ResolveDeadline));
        AssertRefused(options => options.ResolveDeadline = TimeSpan.FromSeconds(301), nameof(TemplateSourceOptions.ResolveDeadline));
        AssertRefused(options => options.MaxFiles = 0, nameof(TemplateSourceOptions.MaxFiles));
        AssertRefused(options => options.MaxFiles = 257, nameof(TemplateSourceOptions.MaxFiles));
        AssertRefused(options => options.MaxFileBytes = 1023, nameof(TemplateSourceOptions.MaxFileBytes));
        AssertRefused(options => options.MaxFileBytes = (8 * 1024 * 1024) + 1, nameof(TemplateSourceOptions.MaxFileBytes));
        AssertRefused(options => options.MaxPackageBytes = 0, nameof(TemplateSourceOptions.MaxPackageBytes));
        AssertRefused(options => options.MaxPackageBytes = (32 * 1024 * 1024) + 1, nameof(TemplateSourceOptions.MaxPackageBytes));
    }

    [Fact]
    public void TheResolutionDeadlineDefaultsToFourMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(240), new TemplateSourceOptions().ResolveDeadline);
        Assert.Equal(5, TemplateSourceOptions.MinResolveDeadlineSeconds);
        Assert.Equal(300, TemplateSourceOptions.MaxResolveDeadlineSeconds);
    }

    [Fact]
    public void TheTokenIsNeverNamedByAProblemOrItsCause()
    {
        var options = Valid();
        options.Token = PlantedToken;
        options.Repository = string.Empty;

        var problems = options.Validate().ToList();

        Assert.NotEmpty(problems);
        foreach (var problem in problems)
        {
            Assert.DoesNotContain(PlantedToken, problem.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(PlantedToken, problem.Reason, StringComparison.Ordinal);
        }

        Assert.True(options.HasToken);
        Assert.Equal(string.Empty, options.Token.Replace(PlantedToken, string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void TheOwnerAndTheNameAreReadFromTheRepository()
    {
        var options = Valid();

        Assert.Equal("report-service-tests", options.Owner);
        Assert.Equal("template-source", options.RepositoryName);
        Assert.False(options.HasToken);

        options.Repository = "no-separator";
        Assert.Equal(string.Empty, options.Owner);
        Assert.Equal(string.Empty, options.RepositoryName);
    }

    private static TemplateSourceOptions Valid() => new()
    {
        Repository = "report-service-tests/template-source",
        Branch = "main",
    };

    private static void AssertRefused(Action<TemplateSourceOptions> change, string setting)
    {
        var options = Valid();
        change(options);

        Assert.Contains(options.Validate(), problem => problem.Setting == setting);
    }
}
