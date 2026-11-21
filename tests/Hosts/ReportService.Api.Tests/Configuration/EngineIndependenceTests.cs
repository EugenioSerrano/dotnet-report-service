using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests.Configuration;

/// <summary>The API and the shared service conventions carry no workflow-engine dependency.</summary>
public sealed class EngineIndependenceTests
{
    [Fact]
    public void ApiAssemblyReferencesNoWorkflowEngineAssembly()
    {
        var references = typeof(Program).Assembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain(references, name => name is not null && name.StartsWith("Temporalio", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name == "ReportService.ServiceDefaults.Temporal");
    }

    [Fact]
    public void SharedServiceConventionsReferenceNoWorkflowEngineAssembly()
    {
        var references = typeof(ConfigurationError).Assembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain(references, name => name is not null && name.StartsWith("Temporalio", StringComparison.Ordinal));
    }
}
