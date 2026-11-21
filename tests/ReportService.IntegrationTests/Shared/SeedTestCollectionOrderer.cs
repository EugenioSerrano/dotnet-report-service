using System.Globalization;
using Xunit.Sdk;
using Xunit.v3;

namespace ReportService.IntegrationTests.Shared;

/// <summary>
/// Orders the whole test collections (one per test class, by default) of the integration
/// suite by a seed, so a covering run can execute the classes in a genuinely different
/// order and surface order-dependent cross-test contamination (SPEC AC-4).
/// <para>
/// xUnit v3's built-in run seed randomizes test-case order <b>within</b> a collection but
/// does not reorder whole collections/classes (demonstrated in the V-Bounce), so this
/// orderer is the SPEC's named fallback. The seed is read from the environment variable
/// <c>REPORTSERVICE_IT_SEED</c>: with no seed the order is the deterministic base order
/// (collections sorted by their unique id); with a seed the base order is shuffled
/// reproducibly. The covering run sets the variable and records the exact value, so the
/// ordering can be reproduced on demand.
/// </para>
/// </summary>
public sealed class SeedTestCollectionOrderer : ITestCollectionOrderer
{
    /// <summary>The environment variable the covering run sets to the seed it records.</summary>
    public const string SeedVariable = "REPORTSERVICE_IT_SEED";

    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : ITestCollection
    {
        // A deterministic base order, independent of the order discovery handed them in, so
        // the same seed always produces the same run order.
        var ordered = testCollections
            .OrderBy(collection => collection.UniqueID, StringComparer.Ordinal)
            .ToList();

        var seedText = Environment.GetEnvironmentVariable(SeedVariable);
        if (!int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
        {
            return ordered;
        }

        // Fisher-Yates over the deterministic base order, seeded so the order is reproducible.
        var random = new Random(seed);
        for (var index = ordered.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (ordered[index], ordered[swap]) = (ordered[swap], ordered[index]);
        }

        return ordered;
    }
}
