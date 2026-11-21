namespace ReportService.DataContract.Tests;

/// <summary>
/// The prepared contract cache: a schema is compiled once per package and schema
/// path however often it is evaluated, an evicted contract is disposed only when
/// nobody is using it, and concurrent evaluation of one contract gives the same
/// verdicts.
/// </summary>
public sealed class PreparedDataContractCacheTests
{
    private static readonly byte[] Schema = Schemas.Utf8(Schemas.ValidEnvelope);
    private static readonly byte[] Record = Schemas.Utf8("""{"anything":1}""");

    [Fact]
    public void ASchemaIsCompiledOnceAcrossManyEvaluationsOfBothRoots()
    {
        var cache = new PreparedDataContractCache();

        for (var evaluation = 0; evaluation < 1_000; evaluation++)
        {
            using var lease = cache.Lease("sha-a", "schema.json", Schema);
            Assert.True(lease.Compilation.Contract!.CheckHeader(Record).IsValid);
            Assert.True(lease.Compilation.Contract.CheckRecord(Record).IsValid);
        }

        Assert.Equal(1, cache.CompilationCount);
    }

    [Fact]
    public void ADifferentPackageOrSchemaPathIsCompiledSeparately()
    {
        var cache = new PreparedDataContractCache();

        using (cache.Lease("sha-a", "schema.json", Schema))
        {
        }

        using (cache.Lease("sha-b", "schema.json", Schema))
        {
        }

        using (cache.Lease("sha-a", "other.json", Schema))
        {
        }

        Assert.Equal(3, cache.CompilationCount);
    }

    [Fact]
    public void AnEvictedContractStillInUseIsDisposedOnlyWhenItsLeaseEnds()
    {
        var cache = new PreparedDataContractCache();

        var first = cache.Lease("sha-a", "schema.json", Schema);
        var firstContract = first.Compilation.Contract!;

        for (var index = 0; index < PreparedDataContractCache.MaximumEntries; index++)
        {
            using var other = cache.Lease("sha-" + index, "schema.json", Schema);
        }

        Assert.False(firstContract.IsDisposed);

        first.Dispose();

        Assert.True(firstContract.IsDisposed);
    }

    [Fact]
    public void AnEvictedContractNobodyIsUsingIsDisposedAtOnce()
    {
        var cache = new PreparedDataContractCache();
        PreparedDataContract evicted;

        using (var first = cache.Lease("sha-a", "schema.json", Schema))
        {
            evicted = first.Compilation.Contract!;
        }

        using (cache.Lease("sha-b", "schema.json", Schema))
        {
        }

        for (var index = 0; index < PreparedDataContractCache.MaximumEntries + 2; index++)
        {
            using var other = cache.Lease("sha-fill-" + index, "schema.json", Schema);
        }

        Assert.True(evicted.IsDisposed);
    }

    [Fact]
    public void ARefusedSchemaIsLeasedAsARefusalAndNeverBecomesAContract()
    {
        var cache = new PreparedDataContractCache();

        using var lease = cache.Lease("sha-a", "schema.json", Schemas.Utf8("""{"not":"an envelope"}"""));

        Assert.False(lease.Compilation.Profile.IsValid);
        Assert.Null(lease.Compilation.Contract);
        Assert.Equal(1, cache.CompilationCount);
    }

    [Fact]
    public void ConcurrentEvaluationOfOneContractGivesTheSameVerdicts()
    {
        var cache = new PreparedDataContractCache();
        using var lease = cache.Lease("sha-a", "schema.json", Schema);
        var contract = lease.Compilation.Contract!;

        var verdicts = new bool[4][];
        Parallel.For(0, 4, worker =>
        {
            var results = new bool[100];
            for (var index = 0; index < results.Length; index++)
            {
                results[index] = contract.CheckRecord(Record).IsValid;
            }

            verdicts[worker] = results;
        });

        foreach (var worker in verdicts)
        {
            Assert.All(worker, Assert.True);
        }

        Assert.Equal(1, cache.CompilationCount);
    }
}
