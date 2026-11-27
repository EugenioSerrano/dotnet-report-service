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

    /// <summary>The groups the over-ceiling snapshot holds: enough blocks to cross the page limit.</summary>
    public const int OverCeilingGroups = 47;

    /// <summary>The records one over-ceiling group holds: one block at the governed limits.</summary>
    public const int OverCeilingGroupSize = 1_300;

    /// <summary>The records the over-ceiling snapshot holds.</summary>
    public const int OverCeilingRecords = OverCeilingGroups * OverCeilingGroupSize;

    /// <summary>The published page limit the over-ceiling fixture is derived to cross.</summary>
    public const int OverCeilingPageLimit = 10_000;

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
    /// The over-ceiling snapshot, derived from the published page limit, never guessed:
    /// <see cref="OverCeilingGroups"/> groups of <see cref="OverCeilingGroupSize"/> records
    /// each. Under the governed soft target the cut waits for a group boundary, so one
    /// group is one block, and <see cref="OverCeilingPackage"/> prints six rows to a page:
    /// 1 300 records give about 217 pages a block - inside the block's 224-page envelope -
    /// and 47 blocks cross the 10 000-page limit before their part bytes cross the byte
    /// limit (the pages carry six short rows, well under the ceiling's own relation of
    /// 37 748 736 bytes to 10 001 pages). The oracle is empty: the case asserts the
    /// refusal, not the totals of a document that is never published.
    /// </summary>
    public static (byte[] Snapshot, IReadOnlyList<AccountedRecord> Records) OverCeilingSnapshot()
    {
        var items = new JsonArray();
        long sequence = 0;
        for (var group = 1; group <= OverCeilingGroups; group++)
        {
            var groupId = "G" + group.ToString("D4", CultureInfo.InvariantCulture);
            for (var index = 0; index < OverCeilingGroupSize; index++)
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
            ["reportTitle"] = "Over-ceiling statement",
            ["currency"] = "ARS",
            ["totals"] = new JsonObject { ["recordCount"] = OverCeilingRecords },
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
    /// The over-ceiling package: the same accounting package with rows tall enough to print
    /// six to a page, so the page limit is the one the fixture crosses. The row keeps its
    /// accounting marks and its no-split rule; only its height changes.
    /// </summary>
    /// <param name="owner">The consumer that owns the version.</param>
    /// <param name="templateId">The template identity the fixture publishes.</param>
    public static Dictionary<string, byte[]> OverCeilingPackage(string owner, string templateId)
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
