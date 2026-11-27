using ReportService.Data.Partitioning;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// Maps a taken template version's typed manifest to the partition engine's spec: the
/// field whose value is a record's group key, the field whose value is the page-totals
/// amount, and the governed block limits. Both fields are optional by the manifest's own
/// contract: a package that declares no group key is cut only by the hard maximum, and
/// one that declares no amount field carries a zero opening carried-forward. The limits
/// are product constants of the partitioner version and never a per-job option.
/// </summary>
internal static class TemplatePartitionSpec
{
    /// <summary>The spec the package's own declarations describe, under the governed limits.</summary>
    /// <param name="manifest">The typed manifest of the version the job is pinned to.</param>
    /// <returns>The spec the streaming partition pass applies.</returns>
    public static PartitionSpec From(TemplateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new PartitionSpec(
            manifest.Partitioning?.GroupKey,
            manifest.Outputs?.Pdf?.PageTotals?.AmountField,
            PartitionLimits.Default);
    }
}
