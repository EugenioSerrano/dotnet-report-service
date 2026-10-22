using XlsxWriterSpike.Typing;

namespace XlsxWriterSpike.Writers;

/// <summary>What happens when the records do not fit one sheet.</summary>
internal enum RowLimitPolicy
{
    /// <summary>Fail with <c>OUTPUT_LIMIT_EXCEEDED</c> (default).</summary>
    Error,

    /// <summary>Continue in additional sheets <c>items-2</c>, <c>items-3</c>, ...</summary>
    Split,
}

/// <summary>One sheet of the output: which records of the source it holds.</summary>
/// <param name="Name">Sheet name.</param>
/// <param name="Skip">Records of the source before this sheet.</param>
/// <param name="Take">Records in this sheet, or <c>null</c> to read up to the end of the source.</param>
internal sealed record SheetWindow(string Name, long Skip, long? Take);

/// <summary>
/// Splits the source into sheets according to the row limit and the policy.
/// The streaming guard (<see cref="RowLimit"/>) is applied to every sheet in
/// addition to the plan, so a header that understates the record count can
/// never cause a silent truncation.
/// </summary>
internal sealed class SheetPlan
{
    /// <summary>1 048 576 rows per sheet minus the header row.</summary>
    public const long MaxDataRowsPerSheet = 1_048_575;

    private SheetPlan(long rowLimit, RowLimitPolicy policy, IReadOnlyList<SheetWindow> sheets)
    {
        RowLimit = rowLimit;
        Policy = policy;
        Sheets = sheets;
    }

    /// <summary>Most data rows in one sheet.</summary>
    public long RowLimit { get; }

    /// <summary>Policy applied.</summary>
    public RowLimitPolicy Policy { get; }

    /// <summary>Sheets in order.</summary>
    public IReadOnlyList<SheetWindow> Sheets { get; }

    /// <summary>Builds the plan; with policy <c>error</c> a known count above the limit fails before writing.</summary>
    /// <param name="sheetName">Name of the first sheet.</param>
    /// <param name="headerCount"><c>header.totals.recordCount</c> of the source, when present.</param>
    /// <param name="rows">The <c>--rows</c> option, when given.</param>
    /// <param name="rowLimit">Data rows per sheet.</param>
    /// <param name="policy">Row-limit policy.</param>
    /// <param name="ignoreHeaderCount">Do not use the header count (exercises the streaming guard).</param>
    public static SheetPlan Create(string sheetName, long? headerCount, long? rows, long rowLimit, RowLimitPolicy policy, bool ignoreHeaderCount)
    {
        if (rowLimit is < 1 or > MaxDataRowsPerSheet)
        {
            throw new UsageException($"--sheet-row-limit must be between 1 and {MaxDataRowsPerSheet}.");
        }

        long? count = ignoreHeaderCount
            ? rows
            : headerCount is { } h ? (rows is { } r ? Math.Min(h, r) : h) : rows;

        if (policy == RowLimitPolicy.Error)
        {
            if (count is { } known && known > rowLimit)
            {
                throw new OutputLimitExceededException(
                    "rows", rowLimit + 1, "-", known.ToString(System.Globalization.CultureInfo.InvariantCulture), beforeWriting: true);
            }

            return new SheetPlan(rowLimit, policy, [new SheetWindow(sheetName, 0, rows)]);
        }

        if (ignoreHeaderCount || count is null)
        {
            throw new UsageException("Policy 'split' needs the record count (header count or --rows); --ignore-header-count applies to policy 'error'.");
        }

        var total = count.Value;
        var sheetCount = Math.Max(1, (total + rowLimit - 1) / rowLimit);
        var sheets = new List<SheetWindow>();
        for (long i = 0; i < sheetCount; i++)
        {
            var name = i == 0 ? sheetName : $"{sheetName}-{i + 1}";
            var skip = i * rowLimit;
            long? take = i < sheetCount - 1 ? rowLimit : rows is null ? null : total - skip;
            sheets.Add(new SheetWindow(name, skip, take));
        }

        return new SheetPlan(rowLimit, policy, sheets);
    }
}
