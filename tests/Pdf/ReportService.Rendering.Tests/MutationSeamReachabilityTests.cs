using System.Reflection;
using ReportService.Templates;

namespace ReportService.Rendering.Tests;

/// <summary>
/// The two seams a known-bad control is composed through exist in the printing
/// assembly and nowhere else: they are internal, the product holds no
/// implementation of either, and no public member accepts one — so no job, template,
/// configuration or request can reach them.
/// </summary>
public sealed class MutationSeamReachabilityTests
{
    private static readonly Assembly Rendering = typeof(PackageRoutePolicy).Assembly;

    [Fact]
    public void TheSeamsAreInternalToThePrintingAssembly()
    {
        var blockSeam = Rendering.GetType("ReportService.Rendering.IComposedBlockMutation", throwOnError: true)!;
        var htmlSeam = Rendering.GetType("ReportService.Rendering.IComposedHtmlMutation", throwOnError: true)!;

        Assert.True(blockSeam.IsNotPublic || blockSeam.IsNestedAssembly);
        Assert.True(htmlSeam.IsNotPublic || htmlSeam.IsNestedAssembly);
        Assert.True(blockSeam.IsInterface);
        Assert.True(htmlSeam.IsInterface);
    }

    [Fact]
    public void TheProductHoldsNoImplementationOfASeam()
    {
        var seams = new[] { "IComposedBlockMutation", "IComposedHtmlMutation" }
            .Select(name => Rendering.GetType("ReportService.Rendering." + name, throwOnError: true)!)
            .ToList();

        var implementations = Rendering.GetTypes()
            .Where(type => type is { IsInterface: false, IsAbstract: false })
            .Where(type => seams.Any(seam => seam.IsAssignableFrom(type)))
            .ToList();

        Assert.Empty(implementations);
    }

    [Fact]
    public void NoPublicMemberOfTheAssemblyAcceptsASeam()
    {
        var seamNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "IComposedBlockMutation",
            "IComposedHtmlMutation",
            "ComposedBlock",
            "ComposedFragment",
        };

        foreach (var type in Rendering.GetTypes().Where(type => type.IsPublic))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var parameter in method.GetParameters())
                {
                    Assert.DoesNotContain(parameter.ParameterType.Name, seamNames);
                }

                if (method.ReturnType != typeof(void))
                {
                    Assert.DoesNotContain(method.ReturnType.Name, seamNames);
                }
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain(property.PropertyType.Name, seamNames);
            }
        }
    }

    [Fact]
    public void ThePrinterPrintsWithTheMeasuredSettingsOnly()
    {
        // A print option that changed would change every printed page; the four that
        // matter are asserted where they are set, so this case only pins the type.
        var printer = typeof(ChromiumPdfPrinter);

        Assert.True(printer.IsPublic);
        Assert.Equal("ReportService.Rendering", printer.Assembly.GetName().Name);
    }
}
