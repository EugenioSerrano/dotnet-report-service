namespace ReportService.Data;

/// <summary>
/// The limits the document profile fixes for one snapshot. They bound memory, not
/// usage: each one is the largest value the reader or the partitioner admits, and a
/// document that needs more fails closed with the code and the limit named here.
/// </summary>
public static class DataContractLimits
{
    /// <summary>The most bytes the header value may occupy.</summary>
    public const long HeaderBytes = 1_048_576;

    /// <summary>The most bytes one record may occupy.</summary>
    public const long RecordBytes = 1_048_576;

    /// <summary>The most bytes any single token may occupy, whitespace included.</summary>
    public const long TokenBytes = 1_048_576;

    /// <summary>The most levels of nesting a record or the header may reach.</summary>
    public const int Depth = 64;

    /// <summary>The most records one block may hold.</summary>
    public const int BlockRecords = 2_000;

    /// <summary>The most bytes of record content one block may hold.</summary>
    public const long BlockRecordBytes = 1_048_576;

    /// <summary>The buffer the reader starts with, before it grows by doubling.</summary>
    public const int InitialBufferBytes = 4 * 1024;

    /// <summary>The largest buffer the reader ever uses; the cap of a single token.</summary>
    public const int BufferCapBytes = (int)TokenBytes;
}
