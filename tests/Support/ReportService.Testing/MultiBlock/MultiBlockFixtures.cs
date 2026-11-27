using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ReportService.Testing.Fixtures;
using ReportService.Testing.TemplatePackages;

namespace ReportService.Testing.MultiBlock;

/// <summary>
/// The fixtures the live path's multi-block cases share: a grouped snapshot with its
/// independent amount oracle, the package it prints, and the derived over-ceiling fixture.
/// The package is the delivered accounting package of the pilot grouped family - the
/// grouping and the amount field the pilot sample declares, the global numbering contract
/// and the packaged font, and the printed page totals enabled - so the snapshot and the
/// package a hermetic case and an end-to-end case use are built in one place.
/// </summary>
public static class MultiBlockFixtures
{
    /// <summary>The record field the fixture's records are grouped by.</summary>
    public const string GroupKey = PageAccountingFixtures.GroupKey;

    /// <summary>The record field the page totals sum.</summary>
    public const string AmountField = PageAccountingFixtures.AmountField;

    /// <summary>
    /// The records one capacity group holds: above the governed soft target, so under the
    /// group-boundary cut one group is one block, and below the hard maximum.
    /// </summary>
    public const int CapacityGroupSize = 1_100;

    /// <summary>The groups the over-capacity snapshot holds: three blocks, so the crossing comes after a committed block.</summary>
    public const int OverCapacityGroups = 3;

    /// <summary>The records the over-capacity snapshot holds.</summary>
    public const int OverCapacityRecords = OverCapacityGroups * CapacityGroupSize;

    /// <summary>
    /// The page capacity the over-capacity case configures: above one block of
    /// <see cref="CapacityPackage"/> (about 184 pages) and below two, so the second
    /// block's commit is the one that crosses it.
    /// </summary>
    public const int OverCapacityPageLimit = 300;

    /// <summary>The groups the exact-capacity snapshot holds: two blocks, so the document is assembled.</summary>
    public const int ExactCapacityGroups = 2;

    /// <summary>The records the exact-capacity snapshot holds.</summary>
    public const int ExactCapacityRecords = ExactCapacityGroups * CapacityGroupSize;

    /// <summary>
    /// A grouped snapshot of <paramref name="records"/> records and the records' own amount
    /// and group oracle. The pilot grouped dataset supplies the group structure, including
    /// one group larger than a block.
    /// </summary>
    /// <param name="records">How many records the snapshot holds.</param>
    public static (byte[] Snapshot, IReadOnlyList<AccountedRecord> Records) Snapshot(int records) =>
        PageAccountingFixtures.ReadGroupedSlice(records);

    /// <summary>
    /// The multi-block package: the pilot grouped family with the numbering contract, the
    /// packaged font and the printed page totals enabled, as the accounting delivery builds
    /// it.
    /// </summary>
    /// <param name="owner">The consumer that owns the version.</param>
    /// <param name="templateId">The template identity the fixture publishes.</param>
    /// <param name="amountField">The record field the totals sum, or the fixture's own; a non-numeric field is the known-bad control.</param>
    public static Dictionary<string, byte[]> Package(string owner, string templateId, string? amountField = null) =>
        PageAccountingFixtures.Files(owner, templateId, amountField ?? AmountField);

    /// <summary>
    /// A capacity snapshot: <paramref name="groups"/> groups of <see cref="CapacityGroupSize"/>
    /// records each. Under the governed soft target the cut waits for a group boundary, so
    /// one group is one block, and <see cref="CapacityPackage"/> prints six rows to a page:
    /// 1 100 records give about 184 pages a block, inside the block's page envelope, and the
    /// page dimension is reached long before the bytes one. The page count a case relies on
    /// is read from the run, never assumed from this estimate. The oracle is empty: the
    /// cases assert the capacity outcome, not the totals.
    /// </summary>
    /// <param name="groups">How many groups, and so blocks, the snapshot holds.</param>
    public static (byte[] Snapshot, IReadOnlyList<AccountedRecord> Records) CapacitySnapshot(int groups)
    {
        var items = new JsonArray();
        long sequence = 0;
        for (var group = 1; group <= groups; group++)
        {
            var groupId = "G" + group.ToString("D4", CultureInfo.InvariantCulture);
            for (var index = 0; index < CapacityGroupSize; index++)
            {
                sequence++;
                items.Add(new JsonObject
                {
                    ["sequence"] = sequence,
                    ["groupId"] = groupId,
                    ["groupName"] = "Group " + group.ToString(CultureInfo.InvariantCulture),
                    ["date"] = "2026-09-01",
                    ["description"] = "Row " + sequence.ToString(CultureInfo.InvariantCulture),
                    ["amount"] = 1.25m,
                });
            }
        }

        var header = new JsonObject
        {
            ["reportTitle"] = "Capacity statement",
            ["currency"] = "ARS",
            ["totals"] = new JsonObject { ["recordCount"] = groups * CapacityGroupSize },
        };
        var snapshot = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["header"] = header,
            ["items"] = items,
        };
        return (Encoding.UTF8.GetBytes(snapshot.ToJsonString()), []);
    }

    /// <summary>
    /// The capacity package: the same accounting package with rows tall enough to print
    /// six to a page, so the page dimension is the one the capacity cases reach. The row keeps its
    /// accounting marks and its no-split rule; only its height changes.
    /// </summary>
    /// <param name="owner">The consumer that owns the version.</param>
    /// <param name="templateId">The template identity the fixture publishes.</param>
    public static Dictionary<string, byte[]> CapacityPackage(string owner, string templateId)
    {
        var files = PageAccountingFixtures.Files(owner, templateId, AmountField);
        files["html/styles.css"] = Encoding.UTF8.GetBytes(
            PageAccountingFixtures.Stylesheet
            + Environment.NewLine
            + ".item { height: 42mm; }"
            + Environment.NewLine);
        return files;
    }
}
