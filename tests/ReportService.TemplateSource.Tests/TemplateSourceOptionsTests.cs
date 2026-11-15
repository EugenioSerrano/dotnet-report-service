using ReportService.TemplateSource;

namespace ReportService.TemplateSource.Tests;

/// <summary>
/// The settings of the template source: the resolution's deadline and the three
/// package caps, each measured at both ends, and the deadline's default.
/// </summary>
public sealed class TemplateSourceOptionsTests
{
    [Fact]
    public void EveryBoundIsChecked()
    {
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

    private static TemplateSourceOptions Valid() => new();

    private static void AssertRefused(Action<TemplateSourceOptions> change, string setting)
    {
        var options = Valid();
        change(options);

        Assert.Contains(options.Validate(), problem => problem.Setting == setting);
    }
}
