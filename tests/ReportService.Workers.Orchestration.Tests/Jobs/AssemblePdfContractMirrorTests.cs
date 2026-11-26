using System.Reflection;
using ReportService.Workers.Merge.Assembly;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The orchestration's view of the assembly step against the delivered one: the
/// queue, the policy values and every record member, compared member for member. A
/// drift is a stop at the V-Bounce, never a silent retry in production.
/// </summary>
public sealed class AssemblePdfContractMirrorTests
{
    [Fact]
    public void TheActivityNameAndQueueMatchTheDeliveredOnes()
    {
        Assert.Equal(AssemblePdfActivity.Name, AssemblePdfStepContracts.AssemblePdf);
        Assert.Equal(AssemblyStepOptions.Queue, AssemblePdfStepOptions.Queue);
    }

    [Fact]
    public void TheExecutionSettingsMatchTheDeliveredFactory()
    {
        var delivered = AssemblyStepOptions.Create();
        var mirrored = AssemblePdfStepOptions.Create();

        Assert.Equal(delivered.TaskQueue, mirrored.TaskQueue);
        Assert.Equal(delivered.HeartbeatTimeout, mirrored.HeartbeatTimeout);
        Assert.Equal(delivered.StartToCloseTimeout, mirrored.StartToCloseTimeout);
        Assert.Equal(delivered.RetryPolicy!.InitialInterval, mirrored.RetryPolicy!.InitialInterval);
        Assert.Equal(delivered.RetryPolicy.BackoffCoefficient, mirrored.RetryPolicy.BackoffCoefficient);
        Assert.Equal(delivered.RetryPolicy.MaximumInterval, mirrored.RetryPolicy.MaximumInterval);
        Assert.Equal(delivered.RetryPolicy.MaximumAttempts, mirrored.RetryPolicy.MaximumAttempts);
        Assert.Equal(delivered.RetryPolicy.NonRetryableErrorTypes, mirrored.RetryPolicy.NonRetryableErrorTypes);
        Assert.Equal(AssemblyStepOptions.RetryPolicySummary, AssemblePdfStepOptions.RetryPolicySummary);
    }

    [Fact]
    public void TheInputRecordMirrorsTheDeliveredOne() =>
        AssertMirrored(typeof(AssemblePdfInput), typeof(AssemblePdfStepInput));

    [Fact]
    public void TheResultRecordMirrorsTheDeliveredOne() =>
        AssertMirrored(typeof(AssemblePdfResult), typeof(AssemblePdfStepResult));

    [Fact]
    public void TheActivityInterfaceNamesTheWorkerContract()
    {
        var name = typeof(IAssemblePdfStep)
            .GetMethod(nameof(IAssemblePdfStep.RunAsync))!
            .GetCustomAttribute<Temporalio.Activities.ActivityAttribute>()!
            .Name;
        Assert.Equal(AssemblePdfActivity.Name, name);
    }

    private static void AssertMirrored(Type delivered, Type mirrored)
    {
        var deliveredMembers = delivered.GetProperties()
            .Select(property => (property.Name, property.PropertyType))
            .OrderBy(member => member.Name, StringComparer.Ordinal)
            .ToList();
        var mirroredMembers = mirrored.GetProperties()
            .Select(property => (property.Name, property.PropertyType))
            .OrderBy(member => member.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(deliveredMembers, mirroredMembers);
    }
}
