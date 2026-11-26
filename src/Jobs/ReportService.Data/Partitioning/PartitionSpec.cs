namespace ReportService.Data.Partitioning;

/// <summary>
/// What one streaming partition pass is told about the data it cuts: the field whose
/// value is the group key (null when the data is not grouped), the field whose value is
/// the page-totals amount (null when no total is carried), and the block limits the cut
/// applies. The field names come from the template manifest; this type carries no value.
/// </summary>
/// <param name="GroupKeyField">The field a record's group key lives under, or null when the data is not grouped.</param>
/// <param name="AmountField">The field a record's page-totals amount lives under, or null when none is carried.</param>
/// <param name="Limits">The block-limit policy the cut applies.</param>
public sealed record PartitionSpec(string? GroupKeyField, string? AmountField, PartitionLimits Limits);
