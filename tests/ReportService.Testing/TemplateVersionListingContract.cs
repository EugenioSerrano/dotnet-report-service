using ReportService.TemplateSource;

namespace ReportService.Testing;

/// <summary>
/// The contract every template source's version listing is held to: the client
/// against the source's documented answers, and the in-memory double the unit
/// level reads through. A template folder that does not exist answers absent,
/// never empty; a folder with no version folder answers an empty list; a folder
/// whose name is not a version still answers its name, because which name is a
/// version is the resolution's rule and never the client's; and an answer the
/// source truncated is refused as too large, never as a version that is absent.
/// </summary>
public abstract class TemplateVersionListingContract
{
    /// <summary>The source the cases of this contract run against.</summary>
    protected abstract ITemplateSource Source { get; }

    /// <summary>Records a version folder under <c>templates/&lt;templateId&gt;/&lt;version&gt;/</c>.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version, or any folder name the case wants.</param>
    protected abstract void SeedVersionFolder(string templateId, string version);

    /// <summary>Records a template folder that holds no version folder.</summary>
    /// <param name="templateId">The template's identifier.</param>
    protected abstract void SeedTemplateFolder(string templateId);

    /// <summary>Records a file directly under the template folder.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="name">The file's name.</param>
    protected abstract void SeedFileInTemplateFolder(string templateId, string name);

    /// <summary>Answers the template folder's tree as truncated.</summary>
    /// <param name="templateId">The template's identifier.</param>
    protected abstract void TruncateTemplateFolder(string templateId);

    [Fact]
    public async Task TheVersionFoldersOfATemplateComeBackByName()
    {
        SeedVersionFolder("invoice-energy", "1.0.0");
        SeedVersionFolder("invoice-energy", "1.1.0");
        var commit = await Source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var versions = await Source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken);

        Assert.NotNull(versions);
        Assert.Equal(["1.0.0", "1.1.0"], versions.Names);
    }

    [Fact]
    public async Task ATemplateThatDoesNotExistAnswersAbsent()
    {
        SeedVersionFolder("invoice-energy", "1.0.0");
        var commit = await Source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var versions = await Source.ListVersionsAsync(commit, "no-such-template", TestContext.Current.CancellationToken);

        Assert.Null(versions);
    }

    [Fact]
    public async Task ATemplateFolderWithNoVersionAnswersAnEmptyList()
    {
        SeedTemplateFolder("invoice-energy");
        var commit = await Source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var versions = await Source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken);

        Assert.NotNull(versions);
        Assert.Empty(versions.Names);
    }

    [Fact]
    public async Task AFolderNameThatIsNotAVersionIsStillAName()
    {
        SeedVersionFolder("invoice-energy", "v1.3.0");
        var commit = await Source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var versions = await Source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken);

        Assert.NotNull(versions);
        Assert.Equal(["v1.3.0"], versions.Names);
    }

    [Fact]
    public async Task AFileBesideTheVersionFoldersIsNotAVersion()
    {
        SeedVersionFolder("invoice-energy", "1.0.0");
        SeedFileInTemplateFolder("invoice-energy", "README.md");
        var commit = await Source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var versions = await Source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken);

        Assert.NotNull(versions);
        Assert.Equal(["1.0.0"], versions.Names);
    }

    [Fact]
    public async Task ATemplateFolderOfAnotherTemplateIsNotListed()
    {
        SeedVersionFolder("invoice-energy", "1.0.0");
        SeedVersionFolder("invoice-other", "2.0.0");
        var commit = await Source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var versions = await Source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken);

        Assert.NotNull(versions);
        Assert.Equal(["1.0.0"], versions.Names);
    }

    [Fact]
    public async Task TheAnswerIsRepeatableWithoutReadingTheSourceAgainInADifferentOrder()
    {
        SeedVersionFolder("invoice-energy", "1.1.0");
        SeedVersionFolder("invoice-energy", "1.0.0");
        var commit = await Source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var first = await Source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken);
        var second = await Source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Names, second.Names);
    }

    [Fact]
    public async Task ATruncatedTemplateFolderIsRefusedAsTooLarge()
    {
        SeedVersionFolder("invoice-energy", "1.0.0");
        TruncateTemplateFolder("invoice-energy");
        var commit = await Source.ResolveBranchAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<TemplateSourceException>(
            () => Source.ListVersionsAsync(commit, "invoice-energy", TestContext.Current.CancellationToken));

        Assert.Equal(TemplateSourceFailure.PackageTooLarge, exception.Failure);
    }
}
