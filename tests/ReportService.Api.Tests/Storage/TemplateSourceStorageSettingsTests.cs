using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests.Storage;

/// <summary>
/// The template source's own settings section: the root is required, the read
/// buffer has a floor and a ceiling, and a missing root may never be created
/// for a reader. Every problem names its setting and never its value.
/// </summary>
public sealed class TemplateSourceStorageSettingsTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private string Root => Path.Combine(_directory.Path, "template-source");

    [Fact]
    public void AValidSectionHasNoProblem()
    {
        var settings = new TemplateSourceStorageSettings
        {
            Filesystem = { Root = Root, BufferBytes = 64 * 1024, CreateIfMissing = false },
        };

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void TheRootIsRequired()
    {
        var settings = new TemplateSourceStorageSettings();

        var problem = Assert.Single(settings.Validate());

        Assert.Equal("Filesystem:Root", problem.Setting);
        Assert.Equal("is required", problem.Reason);
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(1048577)]
    public void TheBufferIsInsideItsBound(int bufferBytes)
    {
        var settings = new TemplateSourceStorageSettings { Filesystem = { Root = Root, BufferBytes = bufferBytes } };

        var problem = Assert.Single(settings.Validate());

        Assert.Equal("Filesystem:BufferBytes", problem.Setting);
        Assert.Equal("must be between 4096 and 1048576", problem.Reason);
    }

    [Fact]
    public void AMissingRootMayNotBeCreated()
    {
        var settings = new TemplateSourceStorageSettings { Filesystem = { Root = Root, CreateIfMissing = true } };

        var problem = Assert.Single(settings.Validate());

        Assert.Equal("Filesystem:CreateIfMissing", problem.Setting);
        Assert.Contains("must be false", problem.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoProblemCarriesTheValueItRefuses()
    {
        var settings = new TemplateSourceStorageSettings { Filesystem = { Root = Root, BufferBytes = 1, CreateIfMissing = true } };

        var problems = settings.Validate().ToList();

        Assert.Equal(2, problems.Count);
        Assert.All(problems, problem => Assert.DoesNotContain(Root, problem.ToString(), StringComparison.OrdinalIgnoreCase));
    }
}
