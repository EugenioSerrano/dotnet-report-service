namespace ReportService.Templates;

/// <summary>
/// Which of a block's hard maximums a sample crossed. The record count is tested
/// first, as the partitioner tests it, so a sample over both names the count.
/// </summary>
public enum SampleBound
{
    /// <summary>More records than one block may hold.</summary>
    Records,

    /// <summary>More bytes of record content than one block may hold.</summary>
    RecordBytes,
}

/// <summary>
/// What reading a package's <c>sample.json</c> answered: the sample as one block,
/// the bound it crossed, or the format read that could not read it. The over-bound
/// counts are the sample's own - how many records it holds and how many bytes of
/// record content they occupy - and never a value of the data. The unreadable
/// reason is the delivered reader's own code, never a value.
/// </summary>
public abstract record SampleRead
{
    /// <summary>The sample is a block a job's partitioner could produce.</summary>
    /// <param name="Sample">The sample as one block.</param>
    public sealed record Partitioned(SamplePartition Sample) : SampleRead;

    /// <summary>The sample is larger than one block and is refused before anything is composed.</summary>
    /// <param name="Bound">The bound that was crossed.</param>
    /// <param name="Records">How many records the sample holds.</param>
    /// <param name="RecordBytes">How many bytes of record content it holds.</param>
    public sealed record OverBound(SampleBound Bound, int Records, long RecordBytes) : SampleRead;

    /// <summary>The sample does not follow the data format the delivered reader reads.</summary>
    /// <param name="Reason">The reader's own reason code, never a value.</param>
    public sealed record Unreadable(string Reason) : SampleRead;
}
