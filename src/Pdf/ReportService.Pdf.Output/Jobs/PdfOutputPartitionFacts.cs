namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What reading a job's PDF partition facts is asked for.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record PdfOutputPartitionFactsInput(string ReportJobId);

/// <summary>The identifiers and counts of a job's partition, and nothing of its data.</summary>
/// <param name="BlockCount">How many blocks the partition holds.</param>
/// <param name="RecordCount">How many records the partition holds.</param>
public sealed record PdfOutputPartitionFactsResult(int BlockCount, long RecordCount);

/// <summary>The activity a job's PDF output reads its partition facts through.</summary>
public static class PdfOutputPartitionFacts
{
    /// <summary>The name the partition-facts read is registered and scheduled under.</summary>
    public const string Name = "ReadPdfOutputPartitionFacts";
}
