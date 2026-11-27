using ReportService.Pdf.Assembly;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The assembly's fixed method bounds: batching defaults to 25 consecutive parts, and
/// the temporary disk factor is three times the parts' own bytes. A document's size is
/// not among them - it is the deployment's configured capacity.
/// </summary>
public sealed class AssemblyLimitsTests
{
    [Fact]
    public void The_declared_bounds_are_the_architecture_s()
    {
        Assert.Equal(25, AssemblyLimits.BatchSize);
        Assert.Equal(3, AssemblyLimits.TemporaryDiskFactor);
    }
}
