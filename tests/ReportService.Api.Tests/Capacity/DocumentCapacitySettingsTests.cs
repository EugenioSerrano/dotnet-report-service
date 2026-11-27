using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.Api.Tests.Capacity;

/// <summary>
/// The deployment's document capacity settings: none configured runs the example; a
/// configured value must be positive and comes with the deployment's own profile name;
/// a refused setting stops the process at start, naming the setting and never its value.
/// </summary>
public sealed class DocumentCapacitySettingsTests
{
    [Fact]
    public void NoSettingRunsTheExampleProfile()
    {
        var settings = new DocumentCapacitySettings();

        Assert.Empty(settings.Validate());
        Assert.Equal(
            DocumentCapacity.Example,
            DocumentCapacity.From(settings.MaximumPages, settings.MaximumPartBytes, settings.ProfileName));
        Assert.Equal(10_000, DocumentCapacity.Example.MaximumPages);
        Assert.Equal(37_748_736L, DocumentCapacity.Example.MaximumPartBytes);
        Assert.Equal("example-reference-10k", DocumentCapacity.Example.ProfileName);
    }

    [Fact]
    public void AConfiguredProfileReplacesTheExampleValueByValue()
    {
        var settings = new DocumentCapacitySettings { MaximumPages = 50_000, ProfileName = "reference-50k" };

        Assert.Empty(settings.Validate());
        Assert.Equal(
            new DocumentCapacity(50_000, DocumentCapacity.ExamplePartBytes, "reference-50k"),
            DocumentCapacity.From(settings.MaximumPages, settings.MaximumPartBytes, settings.ProfileName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void APageCapacityMustBeGreaterThanZero(int pages)
    {
        var problem = Assert.Single(new DocumentCapacitySettings { MaximumPages = pages, ProfileName = "p" }.Validate());

        Assert.Equal(DocumentCapacitySettings.MaximumPagesSetting, problem.Setting);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void ABytesCapacityMustBeGreaterThanZero(long bytes)
    {
        var problem = Assert.Single(new DocumentCapacitySettings { MaximumPartBytes = bytes, ProfileName = "p" }.Validate());

        Assert.Equal(DocumentCapacitySettings.MaximumPartBytesSetting, problem.Setting);
    }

    [Fact]
    public void AConfiguredValueWithoutAProfileNameIsRefused()
    {
        var problem = Assert.Single(new DocumentCapacitySettings { MaximumPages = 20_000 }.Validate());

        Assert.Equal(DocumentCapacitySettings.ProfileNameSetting, problem.Setting);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("perfil-ñ")]
    public void AProfileNameMustBePrintableAsciiWithoutSpaces(string name)
    {
        var problem = Assert.Single(new DocumentCapacitySettings { ProfileName = name }.Validate());

        Assert.Equal(DocumentCapacitySettings.ProfileNameSetting, problem.Setting);
    }

    [Fact]
    public void AProfileNameLongerThanSixtyFourCharactersIsRefused()
    {
        Assert.Empty(new DocumentCapacitySettings { ProfileName = new string('a', 64) }.Validate());
        var problem = Assert.Single(new DocumentCapacitySettings { ProfileName = new string('a', 65) }.Validate());

        Assert.Equal(DocumentCapacitySettings.ProfileNameSetting, problem.Setting);
    }

    [Fact]
    public void TheApiStopsAtStartForAnInvalidCapacityNamingTheSettingAndNotTheValue()
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DocumentCapacity:MaximumPages"] = "-77",
            ["DocumentCapacity:ProfileName"] = "probe",
            ["Storage:Filesystem:Root"] = Path.Combine(Path.GetTempPath(), "capacity-" + Guid.NewGuid().ToString("N")),
            [DatabaseOptions.ConnectionStringSetting] = MetadataDatabaseStub.ConnectionString,
            [DatabaseOptions.ExpectedSchemaVersionSetting] = MetadataDatabaseStub.ExpectedVersion,
        };
        using var probe = StartFailureProbe.Create(settings, replacements: services => MetadataDatabaseStub.UseHealthy(services));

        var error = probe.Refusal();

        Assert.Contains("DocumentCapacity:MaximumPages", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("-77", error.Message, StringComparison.Ordinal);
    }
}
