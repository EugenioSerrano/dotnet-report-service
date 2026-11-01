using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests.Storage;

/// <summary>The storage settings: a provider is required and must be known; the Filesystem settings are checked through it.</summary>
public sealed class ObjectStorageSettingsTests
{
    [Fact]
    public void FilesystemWithARootIsValid()
    {
        var settings = new ObjectStorageSettings { Provider = "filesystem" };
        settings.Filesystem.Root = "data";

        Assert.Empty(settings.Validate());
        Assert.False(settings.Diagnostics.Enabled);
    }

    [Theory]
    [InlineData(null, "is required; no provider is selected by default")]
    [InlineData("", "is required; no provider is selected by default")]
    [InlineData("Filesystem", "is not a known provider (known: filesystem)")]
    [InlineData("s3", "is not a known provider (known: filesystem)")]
    public void ProviderMustBeGivenAndKnown(string? provider, string reason)
    {
        var problem = Assert.Single(new ObjectStorageSettings { Provider = provider }.Validate());

        Assert.Equal("Provider", problem.Setting);
        Assert.Equal(reason, problem.Reason);
    }

    [Fact]
    public void FilesystemProblemsAreNamedUnderTheirSection()
    {
        var settings = new ObjectStorageSettings { Provider = "filesystem" };
        settings.Filesystem.BufferBytes = 1;

        var problems = settings.Validate().ToList();

        Assert.Equal(["Filesystem:Root", "Filesystem:BufferBytes"], problems.Select(p => p.Setting));
    }
}
