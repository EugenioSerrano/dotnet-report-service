namespace ReportService.Data.Partitioning;

/// <summary>
/// What one streaming partition pass produced: the durable, ordered index page of its
/// blocks. The blocks themselves live in the sink the pass wrote them to; this is the
/// reader's map of them — block order, intervals, continuity, carried-forward and the
/// page-boundary layout — written once and read back exactly.
/// </summary>
/// <param name="Index">The ordered index page of the partition's blocks.</param>
public sealed record StreamingPartition(BlockIndexPage Index);
