using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Testing;

namespace ReportService.Api.Tests.Templates;

/// <summary>
/// What the API is allowed to reach from the owner's read: the record and the store
/// of a taken version, never the template source. The take is a job step of the PDF
/// worker; an API process that could read the repository would be a second reader of
/// the source, which is the one thing the design keeps out of the request path.
/// </summary>
public sealed class TemplateVersionApiReferencesTests
{
    [Fact]
    public void TheApiCarriesTheTakenVersionAndNeverTheTemplateSource()
    {
        var referenced = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.Contains("ReportService.TemplatePackages", referenced);
        Assert.DoesNotContain("ReportService.TemplateSource", referenced);
    }

    [Fact]
    public void TheApiComposesNoServiceOfTheTemplateSourceAndNoClientForIt()
    {
        using var host = new TemplateVersionApiHost();
        var resolvable = host.Services.GetRequiredService<IServiceProviderIsService>();

        // The source's assembly is not part of the API's references, so the only way one of
        // its types could be resolvable is a registration built elsewhere in the composition.
        var sourceTypes = Assembly.Load("ReportService.TemplateSource")
            .GetExportedTypes()
            .Where(type => !type.ContainsGenericParameters);

        Assert.All(sourceTypes, type => Assert.False(resolvable.IsService(type), type.FullName));
        Assert.Null(host.Services.GetService<IHttpClientFactory>());
    }
}
