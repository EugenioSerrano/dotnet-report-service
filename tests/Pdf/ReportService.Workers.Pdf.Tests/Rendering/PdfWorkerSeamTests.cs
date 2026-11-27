using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Rendering;
using ReportService.Templates;
using ReportService.Testing;
using ReportService.Workers.Pdf.Rendering;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The three claims the stall seam is allowed to make, and only these: no type under
/// <c>src/</c> outside <see cref="BlockComposer"/>'s assembly and <see cref="BlockRenderer"/>'s
/// implements <see cref="IRenderStall"/>; no test implementation is reachable from the
/// production composition - the collection <c>AddPdfRenderingWork</c> builds on a fresh
/// <see cref="WebApplicationBuilder"/> resolves a composer with no stall; and no public member
/// of either assembly accepts or returns the seam or its phase.
/// </summary>
public sealed class PdfWorkerSeamTests
{
    private static readonly Assembly Templates = typeof(BlockComposer).Assembly;

    private static readonly Assembly Rendering = typeof(BlockRenderer).Assembly;

    [Fact]
    public void NoTypeUnderSrcOutsideTemplatesAndRenderingMentionsTheSeam()
    {
        var sourceRoot = Path.Combine(RepositoryPaths.Root, "src");
        var allowed = new[]
        {
            Path.Combine(sourceRoot, "Templates", "ReportService.Templates"),
            Path.Combine(sourceRoot, "Pdf", "ReportService.Rendering"),
        };

        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("IRenderStall", StringComparison.Ordinal))
            .Where(file => !allowed.Any(prefix => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoProductionTypeOtherThanTheRenderingAssemblyImplementsTheSeam()
    {
        var seam = Templates.GetType("ReportService.Templates.IRenderStall", throwOnError: true)!;
        var implementations = Templates.GetTypes()
            .Concat(Rendering.GetTypes())
            .Where(type => type is { IsInterface: false, IsAbstract: false })
            .Where(type => seam.IsAssignableFrom(type))
            .ToList();

        Assert.Empty(implementations);
    }

    [Fact]
    public void TheProductionCompositionResolvesAComposerWithNoStall()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = "ReportService.Workers.Pdf",
        });
        builder.AddPdfRenderingWork();

        using var app = builder.Build();
        var composer = app.Services.GetRequiredService<BlockComposer>();

        Assert.Null(composer.Stall);
    }

    [Fact]
    public void NoPublicMemberOfEitherAssemblyAcceptsOrReturnsTheSeam()
    {
        var seamNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "IRenderStall",
            "RenderPhase",
        };

        foreach (var assembly in new[] { Templates, Rendering })
        {
            foreach (var type in assembly.GetTypes().Where(type => type.IsPublic))
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

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    Assert.DoesNotContain(field.FieldType.Name, seamNames);
                }
            }
        }
    }

    [Fact]
    public void TheSeamAndItsPhaseAreInternalToTheTemplatesAssembly()
    {
        var seam = Templates.GetType("ReportService.Templates.IRenderStall", throwOnError: true)!;
        var phase = Templates.GetType("ReportService.Templates.RenderPhase", throwOnError: true)!;

        Assert.True(seam.IsNotPublic || seam.IsNestedAssembly);
        Assert.True(seam.IsInterface);
        Assert.True(phase.IsNotPublic || phase.IsNestedAssembly);
        Assert.True(phase.IsEnum);
        Assert.Equal(["Composition", "Print"], Enum.GetNames(phase));
    }
}
