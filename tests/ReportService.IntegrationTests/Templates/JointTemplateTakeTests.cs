using System.Reflection;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The gate of the joint group. The job-level halves of the version-taking story - a job takes a
/// version it did not have, a second job uses the copy, an outage fails the job with the source's
/// code - belong to the job step that resolves a template, so this group may take part in a run
/// only on a commit that carries that step.
/// <para>
/// The step is <c>ReportJobWorkflow.ResolveTemplateAsync</c>: it is gated by the
/// <c>template-resolve-v1</c> patch, so a recording made before it replays unchanged, and it
/// schedules the take under the name the PDF worker registers it with. On a commit that carries
/// the take but not that step - the take's own branches - there is no job-level half to run, so
/// this case names what is missing and takes no part in the run: a green run never stands for
/// evidence it did not produce.
/// </para>
/// <para>
/// A pass says the step is on this commit, and only that: this class holds this case and no
/// job-level case of its own. The outcomes it describes are run by
/// <c>JobTemplateResolutionTests</c> in the same level, where the product's own API,
/// orchestration worker and PDF worker are hosted together against a real database and engine.
/// </para>
/// </summary>
public sealed class JointTemplateTakeTests
{
    /// <summary>The assembly of the job workflow: the half that runs the job.</summary>
    private const string OrchestrationAssembly = "ReportService.Workers.Orchestration";

    /// <summary>The assembly of the PDF worker: the half that serves the take.</summary>
    private const string PdfAssembly = "ReportService.Workers.Pdf";

    /// <summary>The workflow the job step belongs to.</summary>
    private const string WorkflowType = "ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow";

    /// <summary>The job step itself, after the input wait and before the data step.</summary>
    private const string StepMethod = "ResolveTemplateAsync";

    /// <summary>The field holding the patch that gates the step.</summary>
    private const string PatchField = "TemplateResolvePatch";

    /// <summary>The patch's identifier: the step's own name in the workflow's change history.</summary>
    private const string PatchId = "template-resolve-v1";

    /// <summary>The orchestration worker's view of the two activities the PDF worker serves.</summary>
    private const string ContractsType = "ReportService.Workers.Orchestration.Jobs.TemplateStepContracts";

    /// <summary>The take's activity name in the orchestration worker's mirror.</summary>
    private const string TakeNameField = "TakeTemplateVersion";

    /// <summary>The take as the PDF worker registers it.</summary>
    private const string TakeType = "ReportService.Workers.Pdf.Templates.TakeTemplateVersionActivity";

    /// <summary>The field holding the take's own name in the PDF worker's activity class.</summary>
    private const string TakeTypeField = "Name";

    /// <summary>The take's registered name: the one contract both workers share.</summary>
    private const string TakeName = "TakeTemplateVersion";

    [Fact]
    public void TheJobLevelHalvesRunOnlyOnACommitThatCarriesTheJobStep()
    {
        var absent = JobStepAbsences();
        Assert.SkipUnless(
            absent.Count == 0,
            "the joint group runs only on a commit that carries the job step which resolves a template, and this "
            + "commit does not: " + string.Join("; ", absent) + ".");

        // The step is on this commit. It is gated by its own patch, it is the workflow's
        // ResolveTemplateAsync, and it reaches the take under the name the PDF worker registers.
        var workflow = TypeIn(OrchestrationAssembly, WorkflowType)!;
        var contracts = TypeIn(OrchestrationAssembly, ContractsType)!;
        var take = TypeIn(PdfAssembly, TakeType)!;

        Assert.Equal(PatchId, Constant(workflow, PatchField));
        Assert.NotNull(workflow.GetMethod(StepMethod, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        Assert.Equal(TakeName, Constant(contracts, TakeNameField));
        Assert.Equal(TakeName, Constant(take, TakeTypeField));
    }

    /// <summary>What a commit that carries the take but not the job step is missing, by name.</summary>
    private static IReadOnlyList<string> JobStepAbsences()
    {
        var absent = new List<string>();

        var workflow = TypeIn(OrchestrationAssembly, WorkflowType);
        if (workflow is null)
        {
            absent.Add($"no type '{WorkflowType}'");
        }
        else
        {
            if (workflow.GetMethod(StepMethod, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is null)
            {
                absent.Add($"'{WorkflowType}' declares no '{StepMethod}', the job step that resolves a template");
            }

            if (Constant(workflow, PatchField) is not PatchId)
            {
                absent.Add($"'{WorkflowType}.{PatchField}' is not '{PatchId}', the patch that gates the step");
            }
        }

        var contracts = TypeIn(OrchestrationAssembly, ContractsType);
        if (contracts is null || Constant(contracts, TakeNameField) is not TakeName)
        {
            absent.Add($"'{ContractsType}.{TakeNameField}' is not '{TakeName}', the name the step schedules the take under");
        }

        var take = TypeIn(PdfAssembly, TakeType);
        if (take is null || Constant(take, TakeTypeField) is not TakeName)
        {
            absent.Add($"'{TakeType}.{TakeTypeField}' is not '{TakeName}', the take the step calls");
        }

        return absent;
    }

    /// <summary>The type, when the assembly is on this commit and carries it.</summary>
    private static Type? TypeIn(string assemblyName, string typeName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(assemblyName)).GetType(typeName, throwOnError: false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>The value of a public constant string the type declares, when it declares one.</summary>
    private static string? Constant(Type type, string field) =>
        type.GetField(field, BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string;
}
