using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The no-Chromium guarantee at the unit level (US-008 AC-4, SPEC-261125-1224 §8.1, Phase A.2):
/// the assembly that hosts the HTML assembly activity must not reference
/// <c>ReportService.Rendering</c> (the Chromium/PDF rendering assembly). The guarantee is
/// enforced by the assembly reference graph, read here by reflection over the loaded assemblies —
/// no engine, no container. The observable at the orchestration-history level (no
/// <c>ReportPdfOutput</c> child and no <c>report-pdf-rendering</c> activity for an HTML-only job)
/// is asserted in <c>ReportJobWorkflowHtmlBranchTests</c> (M1). The oracle is the approved intent,
/// never the implementation (G06).
/// </summary>
public sealed class AssembleHtmlNoChromiumTests
{
    private const string ChromiumAssembly = "ReportService.Rendering";

    [Fact]
    public void TheHtmlAssemblyActivityCarriesNoReferenceToTheChromiumRenderingAssembly()
    {
        var hosting = typeof(AssembleHtmlActivity).Assembly;

        Assert.DoesNotContain(
            hosting.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, ChromiumAssembly, StringComparison.Ordinal));
    }

    [Fact]
    public void TheHtmlAssemblyActivityIsRegisteredUnderTheHtmlStepName()
    {
        // The activity the orchestration worker serves on report-html is the AssembleHtml step
        // the HTML output workflow schedules; its registered name is the one the contract names.
        Assert.Equal("AssembleHtml", HtmlOutputStepContracts.AssembleHtml);
        Assert.Contains(
            typeof(AssembleHtmlActivity).GetMethods(),
            method => method.GetCustomAttributes(typeof(Temporalio.Activities.ActivityAttribute), inherit: false)
                .OfType<Temporalio.Activities.ActivityAttribute>()
                .Any(attribute => string.Equals(attribute.Name, HtmlOutputStepContracts.AssembleHtml, StringComparison.Ordinal)));
    }
}
