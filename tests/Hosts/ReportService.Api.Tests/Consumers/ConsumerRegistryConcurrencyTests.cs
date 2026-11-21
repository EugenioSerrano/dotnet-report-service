using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Api.Consumers;

namespace ReportService.Api.Tests.Consumers;

/// <summary>
/// No request sees a partly applied registry: a reader that captured the
/// snapshot before a change keeps a complete, consistent snapshot, and a reader
/// after the change sees the new one. The interleaving is forced with a barrier
/// at every iteration — no sleeps, no timing assumption, no retry.
/// </summary>
public sealed class ConsumerRegistryConcurrencyTests
{
    private const int Iterations = 1_000;

    [Fact]
    public async Task CapturedSnapshotStaysCompleteWhileChangesLandAndLaterReadersSeeTheNewOne()
    {
        var configuration = new ReplaceableConfiguration(Registry(active: true));
        using var registry = new ConsumerRegistry(configuration.Root, NullLogger<ConsumerRegistry>.Instance);
        using var barrier = new Barrier(2);
        var failures = new List<string>();

        var request = Task.Factory.StartNew(
            () =>
            {
                for (var i = 0; i < Iterations; i++)
                {
                    var captured = registry.Current;                 // the request captures once
                    var expectedActive = i % 2 == 0;
                    barrier.SignalAndWait();                         // 1: let the change land
                    barrier.SignalAndWait();                         // 2: the change has landed
                    if (!IsConsistent(captured, expectedActive))
                    {
                        failures.Add($"iteration {i}: the captured snapshot changed");
                    }

                    if (IsConsistent(registry.Current, expectedActive) || !IsConsistent(registry.Current, !expectedActive))
                    {
                        failures.Add($"iteration {i}: a reader after the change did not see the new snapshot");
                    }

                    barrier.SignalAndWait();                         // 3: ready for the next iteration
                }
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var change = Task.Factory.StartNew(
            () =>
            {
                for (var i = 0; i < Iterations; i++)
                {
                    barrier.SignalAndWait();                         // 1
                    configuration.Replace(Registry(active: i % 2 != 0));
                    barrier.SignalAndWait();                         // 2
                    barrier.SignalAndWait();                         // 3
                }
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await Task.WhenAll(request, change);

        Assert.Empty(failures);
    }

    // Every entry carries the same active state, so a mix of two registries is detectable,
    // and the fingerprint must match the entries it was computed from.
    private static bool IsConsistent(ConsumerRegistrySnapshot snapshot, bool active) =>
        snapshot.Entries.Count == 50
        && snapshot.Entries.All(e => e.Active == active)
        && snapshot.Fingerprint == ConsumerRegistrySnapshot.ComputeFingerprint(snapshot.Entries);

    private static Dictionary<string, string?> Registry(bool active)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < 50; i++)
        {
            values[$"consumers:{i}:consumerId"] = $"consumer-{i:D2}";
            values[$"consumers:{i}:active"] = active ? "true" : "false";
        }

        return values;
    }
}
