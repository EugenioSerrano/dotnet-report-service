using ReportService.Testing.Containers;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The restart harness's resolution and interruption window proven over a stubbed
/// container surface: two compositions' merge-worker containers exist on one daemon,
/// and only the composition's own container may be addressed. The resolution is the
/// composition's declared name; a name the runtime does not hold fails loudly with
/// that name, never by falling back to a listed container; the window observes only
/// the own container and starts it only when it is not running.
/// </summary>
public sealed class MergeWorkerContainerTests
{
    private const string Own = "merge-worker-0123456789abcdef";

    private const string Sibling = "merge-worker-fedcba9876543210";

    private static readonly TimeSpan GuardDeadline = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan GuardInterval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task TheResolutionIsTheCompositionsOwnContainerWhenASiblingIsListedFirst()
    {
        var runtime = new StubContainerRuntime()
            .Add(Sibling)
            .Add(Own);

        var container = await MergeWorkerContainer.ResolveAsync(
            runtime,
            Own,
            TestContext.Current.CancellationToken);

        Assert.Equal(Own, container.Name);
        Assert.DoesNotContain(Sibling, runtime.InspectedNames);
        Assert.DoesNotContain(Sibling, runtime.StartedNames);
    }

    [Fact]
    public async Task ADeclaredNameTheRuntimeDoesNotHoldFailsLoudlyNamingIt()
    {
        var runtime = new StubContainerRuntime().Add(Sibling);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MergeWorkerContainer.ResolveAsync(runtime, Own, TestContext.Current.CancellationToken));

        Assert.Contains(Own, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sibling, runtime.InspectedNames);
        Assert.DoesNotContain(Sibling, runtime.StartedNames);
    }

    [Fact]
    public async Task AnotherCompositionsContainerIsNeverAddressedAndItsDisappearanceChangesNothing()
    {
        var runtime = new StubContainerRuntime()
            .Add(Sibling)
            .Add(Own);
        var container = await MergeWorkerContainer.ResolveAsync(
            runtime,
            Own,
            TestContext.Current.CancellationToken);

        runtime.Remove(Sibling);
        runtime.SetRunning(Own, false);
        await RunWindowAsync(container);

        Assert.DoesNotContain(Sibling, runtime.InspectedNames);
        Assert.DoesNotContain(Sibling, runtime.StartedNames);
    }

    [Fact]
    public async Task TheWindowEndsWhenTheOwnContainerStopsAndStartsItExactlyOnce()
    {
        var runtime = new StubContainerRuntime().Add(Own);
        var container = await MergeWorkerContainer.ResolveAsync(
            runtime,
            Own,
            TestContext.Current.CancellationToken);

        runtime.SetRunning(Own, false);
        await RunWindowAsync(container);

        Assert.Equal([Own], runtime.StartedNames);
    }

    [Fact]
    public async Task TheWindowEndsWhenTheOwnContainerIsRestartedWithNoStart()
    {
        var runtime = new StubContainerRuntime().Add(Own);
        var container = await MergeWorkerContainer.ResolveAsync(
            runtime,
            Own,
            TestContext.Current.CancellationToken);

        runtime.SetRestartCount(Own, 1);
        await RunWindowAsync(container);

        Assert.Empty(runtime.StartedNames);
    }

    /// <summary>
    /// The interruption window's steps, as the harness runs them: wait for the own
    /// container to stop or be restarted, start it when it is not running, and wait
    /// for it to run again. Only the own container is ever addressed.
    /// </summary>
    private static async Task RunWindowAsync(MergeWorkerContainer container)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await container.WaitUntilStoppedOrRestartedAsync(
            "the merge worker to stop or be replaced",
            0,
            GuardDeadline,
            GuardInterval,
            cancellationToken);
        if (!await container.IsRunningAsync(cancellationToken))
        {
            await container.StartAsync(cancellationToken);
        }

        await container.WaitUntilRunningAsync(
            "the merge worker to run again",
            GuardDeadline,
            GuardInterval,
            cancellationToken);
    }
}
