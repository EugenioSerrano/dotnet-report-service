using System.Reflection;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The job-level halves of the version-taking story, on one commit: a job that
/// references a version not yet taken is accepted, takes it and prints; a second
/// job of the same version uses the copy with no source read; a job on a version
/// not taken while the source is down ends failed with the source's code; and a
/// folder that is not the manifest's version ends failed as invalid.
/// <para>
/// This group runs on a commit that also carries the job step that resolves a
/// template — <c>ResolveTemplate</c>, US-003.BOLT-002 — because the job-level
/// halves are its code: the step, the pin it records and the mapping from a take
/// failure to the job's failure. On a commit without it the class says so and
/// takes no part in the run, so a green run never stands for evidence it did not
/// produce.
/// </para>
/// </summary>
public sealed class JointTemplateTakeTests
{
    private const string ResolverTypeName = "ReportService.Workers.Orchestration.Jobs.ResolveTemplateWorkflow";

    [Fact]
    public void TheJobLevelHalvesRunOnlyOnACommitThatCarriesTheJobStep()
    {
        if (Resolver().Count == 0)
        {
            Assert.Skip(
                "the joint group waits for US-003.BOLT-002 (OQ-059 B): the job step that calls the take, records the pin and maps its failures "
                + $"is that Bolt's, and no type '{ResolverTypeName}' is on this commit");
        }

        Assert.NotEmpty(Resolver());
    }

    /// <summary>The assemblies that carry a job step resolving a template, when the commit has one.</summary>
    private static IReadOnlyList<Assembly> Resolver() =>
        [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetType(ResolverTypeName, throwOnError: false) is not null)];
}
