using System.Globalization;
using System.Text.Json;

namespace ReportService.Samples.DatasetGenerator;

/// <summary>Result of the streaming check of one dataset file: structural errors and characteristic counters.</summary>
internal sealed class EnvelopeReport
{
    private const int MaxRecordedErrors = 20;
    private readonly List<string> errors = [];

    /// <summary>Structural errors found (at most 20 are kept).</summary>
    public IReadOnlyList<string> Errors => errors;

    /// <summary>Total number of structural errors found.</summary>
    public long ErrorCount { get; private set; }

    /// <summary>Whether the structure is valid.</summary>
    public bool Passed => ErrorCount == 0;

    /// <summary>Value of <c>header.datasetId</c>.</summary>
    public string DatasetId { get; set; } = string.Empty;

    /// <summary>Size of the header object in bytes.</summary>
    public long HeaderBytes { get; set; }

    /// <summary>Size of the largest item in bytes.</summary>
    public long MaxItemBytes { get; set; }

    /// <summary>Number of items read.</summary>
    public long Records { get; set; }

    /// <summary>Number of contiguous group runs.</summary>
    public int Groups { get; set; }

    /// <summary>Size of the smallest group.</summary>
    public long MinGroupSize { get; set; }

    /// <summary>Size of the largest group.</summary>
    public long MaxGroupSize { get; set; }

    /// <summary>Exact sum of the item amounts.</summary>
    public decimal Amount { get; set; }

    /// <summary>Descriptions of at least 1 500 characters.</summary>
    public long LongDescriptions { get; set; }

    /// <summary>Descriptions taken from the hostile list.</summary>
    public long HostileDescriptions { get; set; }

    /// <summary>Descriptions containing non-ASCII characters.</summary>
    public long NonAsciiDescriptions { get; set; }

    /// <summary>Unique descriptions (starting with <c>Ref-</c>).</summary>
    public long UniqueDescriptions { get; set; }

    /// <summary>Formula-like descriptions by first character (<c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>).</summary>
    public Dictionary<char, long> FormulaLikeByPrefix { get; } = new() { ['='] = 0, ['+'] = 0, ['-'] = 0, ['@'] = 0 };

    /// <summary>Items with a negative amount.</summary>
    public long NegativeAmounts { get; set; }

    /// <summary>Items with an <c>accountId</c>.</summary>
    public long AccountIds { get; set; }

    /// <summary>Account identifiers of 20 digits starting with a zero.</summary>
    public long ValidAccountIds { get; set; }

    /// <summary>Items with a <c>quantity</c>.</summary>
    public long Quantities { get; set; }

    /// <summary>Quantities with exactly 16 significant digits.</summary>
    public long SixteenDigitQuantities { get; set; }

    /// <summary>Items with a <c>reference</c>.</summary>
    public long References { get; set; }

    /// <summary>References above 2^53.</summary>
    public long ReferencesAbove2Pow53 { get; set; }

    /// <summary>Initial size of the read buffer.</summary>
    public int InitialBufferSize { get; set; }

    /// <summary>Final size of the read buffer.</summary>
    public int FinalBufferSize { get; set; }

    /// <summary>Number of times the read buffer doubled.</summary>
    public int BufferGrowths { get; set; }

    /// <summary>Records a structural error.</summary>
    public void Fail(string message)
    {
        ErrorCount++;
        if (errors.Count < MaxRecordedErrors)
        {
            errors.Add(message);
        }
    }

    /// <summary>Writes the counters, one group per line.</summary>
    public void WriteCounters(TextWriter output)
    {
        var inv = CultureInfo.InvariantCulture;
        var uniqueShare = Records == 0 ? 0d : (double)UniqueDescriptions / Records;
        output.WriteLine(string.Create(inv, $"  datasetId={DatasetId}  records={Records}  groups={Groups}  amount={Amount}  headerBytes={HeaderBytes}  maxItemBytes={MaxItemBytes}"));
        output.WriteLine(string.Create(inv, $"  groupSize min={MinGroupSize} max={MaxGroupSize}  negativeAmounts={NegativeAmounts}"));
        output.WriteLine(string.Create(inv, $"  descriptions long(>=1500)={LongDescriptions}  hostile={HostileDescriptions}  nonAscii={NonAsciiDescriptions}  unique(Ref-)={UniqueDescriptions} ({uniqueShare:P2})"));
        output.WriteLine(string.Create(inv, $"  formula-like '='={FormulaLikeByPrefix['=']} '+'={FormulaLikeByPrefix['+']} '-'={FormulaLikeByPrefix['-']} '@'={FormulaLikeByPrefix['@']}"));
        output.WriteLine(string.Create(inv, $"  accountId 20 digits with leading zero={ValidAccountIds}/{AccountIds}  quantity 16 significant digits={SixteenDigitQuantities}/{Quantities}  reference>2^53={ReferencesAbove2Pow53}/{References}"));
        output.WriteLine(string.Create(inv, $"  readBuffer initial={InitialBufferSize} final={FinalBufferSize} growths={BufferGrowths}"));
    }
}

/// <summary>
/// Streaming structural check of a <c>header-items-v1</c> file with
/// <see cref="Utf8JsonReader"/>: the document is never parsed as a whole. The
/// read buffer starts at the given size and doubles, up to 2 MiB, when one
/// token does not fit.
/// </summary>
internal sealed class EnvelopeChecker
{
    /// <summary>Default size of the read buffer.</summary>
    public const int DefaultInitialBuffer = 64 * 1024;

    private const int MaxBuffer = 2 * 1024 * 1024;
    private const long MaxObjectBytes = 1024 * 1024;
    private const long TwoPow53 = 9_007_199_254_740_992;
    private static readonly string[] RootProperties = ["contractVersion", "header", "items"];
    private static readonly HashSet<string> Hostile = new(DatasetDefinitions.Hostile, StringComparer.Ordinal);
    private static readonly HashSet<string> FormulaLike = new(DatasetDefinitions.FormulaLike, StringComparer.Ordinal);

    private readonly EnvelopeReport report = new();
    private readonly string?[] names = new string?[64];
    private bool rootStarted;
    private bool rootClosed;
    private int rootPropertyIndex;
    private bool inItems;
    private long headerStart;
    private long itemStart;
    private bool itemHasSequence;
    private long? declaredRecordCount;
    private long? declaredGroupCount;
    private decimal? declaredAmount;
    private string? currentGroup;
    private long currentGroupSize;

    private EnvelopeChecker()
    {
    }

    /// <summary>Checks a file and returns the report.</summary>
    public static EnvelopeReport Check(string path, int initialBuffer = DefaultInitialBuffer)
    {
        var checker = new EnvelopeChecker();
        checker.Run(path, initialBuffer);
        return checker.report;
    }

    private static long SignificantDigits(ReadOnlySpan<byte> number)
    {
        var digits = 0L;
        var leading = true;
        foreach (var b in number)
        {
            if (b is (byte)'e' or (byte)'E')
            {
                return -1;
            }

            if (b is < (byte)'0' or > (byte)'9')
            {
                continue;
            }

            if (leading && b == (byte)'0')
            {
                continue;
            }

            leading = false;
            digits++;
        }

        return digits;
    }

    private static bool IsGreaterGroup(string next, string current) =>
        next.Length != current.Length ? next.Length > current.Length : string.CompareOrdinal(next, current) > 0;

    private void Run(string path, int initialBuffer)
    {
        report.InitialBufferSize = initialBuffer;
        var buffer = new byte[initialBuffer];
        var filled = 0;
        var endOfFile = false;
        var baseOffset = 0L;
        var state = new JsonReaderState(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        try
        {
            filled = Fill(stream, buffer, filled, ref endOfFile);
            while (true)
            {
                var reader = new Utf8JsonReader(buffer.AsSpan(0, filled), endOfFile, state);
                while (reader.Read())
                {
                    Process(ref reader, baseOffset);
                }

                if (endOfFile)
                {
                    break;
                }

                state = reader.CurrentState;
                var consumed = (int)reader.BytesConsumed;
                if (consumed == 0 && filled == buffer.Length)
                {
                    if (buffer.Length >= MaxBuffer)
                    {
                        report.Fail($"A token does not fit in the maximum read buffer of {MaxBuffer} bytes (at byte {baseOffset}).");
                        return;
                    }

                    Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxBuffer));
                    report.BufferGrowths++;
                }
                else
                {
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed);
                    filled -= consumed;
                    baseOffset += consumed;
                }

                filled = Fill(stream, buffer, filled, ref endOfFile);
            }
        }
        catch (JsonException exception)
        {
            report.Fail($"Invalid JSON (read buffer starting at byte {baseOffset}): {exception.Message}");
            return;
        }
        finally
        {
            report.FinalBufferSize = buffer.Length;
        }

        Finish();
    }

    private static int Fill(Stream stream, byte[] buffer, int filled, ref bool endOfFile)
    {
        while (!endOfFile && filled < buffer.Length)
        {
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
            {
                endOfFile = true;
            }

            filled += read;
        }

        return filled;
    }

    private void Process(ref Utf8JsonReader reader, long baseOffset)
    {
        var depth = reader.CurrentDepth;
        if (rootClosed)
        {
            report.Fail("Content after the root object.");
            return;
        }

        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                if (depth == 0)
                {
                    rootStarted = true;
                }
                else if (depth == 1 && names[1] == "header")
                {
                    headerStart = baseOffset + reader.TokenStartIndex;
                }
                else if (depth == 2 && inItems)
                {
                    itemStart = baseOffset + reader.TokenStartIndex;
                    itemHasSequence = false;
                }

                break;
            case JsonTokenType.EndObject:
                if (depth == 0)
                {
                    rootClosed = true;
                    if (rootPropertyIndex != RootProperties.Length)
                    {
                        report.Fail("The root object does not contain contractVersion, header and items.");
                    }
                }
                else if (depth == 1 && names[1] == "header")
                {
                    report.HeaderBytes = baseOffset + reader.BytesConsumed - headerStart;
                    if (report.HeaderBytes > MaxObjectBytes)
                    {
                        report.Fail($"The header is {report.HeaderBytes} bytes, above 1 MiB.");
                    }
                }
                else if (depth == 2 && inItems)
                {
                    EndItem(baseOffset + reader.BytesConsumed - itemStart);
                }

                break;
            case JsonTokenType.StartArray:
                if (depth == 0)
                {
                    report.Fail("The root is not an object.");
                }
                else if (depth == 1 && names[1] == "items")
                {
                    inItems = true;
                }

                break;
            case JsonTokenType.EndArray:
                if (depth == 1)
                {
                    inItems = false;
                }

                break;
            case JsonTokenType.PropertyName:
                var name = reader.GetString();
                names[depth] = name;
                if (depth == 1)
                {
                    if (rootPropertyIndex >= RootProperties.Length || name != RootProperties[rootPropertyIndex])
                    {
                        report.Fail($"Unexpected root property '{name}' (expected exactly contractVersion, header, items in that order).");
                    }

                    rootPropertyIndex++;
                }

                break;
            default:
                Value(ref reader, depth);
                break;
        }
    }

    private void Value(ref Utf8JsonReader reader, int depth)
    {
        if (depth == 0)
        {
            report.Fail("The root is not an object.");
            return;
        }

        switch (names[1])
        {
            case "contractVersion" when depth == 1:
                if (reader.TokenType != JsonTokenType.String || reader.GetString() != "1.0")
                {
                    report.Fail("contractVersion is not \"1.0\".");
                }

                break;
            case "header" when depth == 2 && names[2] == "datasetId":
                report.DatasetId = reader.GetString() ?? string.Empty;
                break;
            case "header" when depth == 3 && names[2] == "totals":
                Totals(ref reader);
                break;
            case "items" when depth == 2:
                report.Fail($"Item {report.Records + 1} is not an object.");
                break;
            case "items" when depth == 3:
                ItemValue(ref reader);
                break;
            default:
                break;
        }
    }

    private void Totals(ref Utf8JsonReader reader)
    {
        switch (names[3])
        {
            case "recordCount" when reader.TryGetInt64(out var recordCount):
                declaredRecordCount = recordCount;
                break;
            case "groupCount" when reader.TryGetInt64(out var groupCount):
                declaredGroupCount = groupCount;
                break;
            case "amount" when reader.TryGetDecimal(out var amount):
                declaredAmount = amount;
                break;
            default:
                break;
        }
    }

    private void ItemValue(ref Utf8JsonReader reader)
    {
        var position = report.Records + 1;
        switch (names[3])
        {
            case "sequence":
                itemHasSequence = true;
                if (!reader.TryGetInt64(out var sequence) || sequence != position)
                {
                    report.Fail($"Item {position} has sequence {Raw(ref reader)}, expected {position}.");
                }

                break;
            case "amount":
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetDecimal(out var amount))
                {
                    report.Amount += amount;
                    if (amount < 0)
                    {
                        report.NegativeAmounts++;
                    }
                }
                else
                {
                    report.Fail($"Item {position} has a non-decimal amount.");
                }

                break;
            case "groupId":
                Group(reader.GetString() ?? string.Empty, position);
                break;
            case "description":
                Description(reader.GetString() ?? string.Empty);
                break;
            case "accountId":
                var accountId = reader.GetString() ?? string.Empty;
                report.AccountIds++;
                if (accountId.Length == 20 && accountId[0] == '0' && accountId.All(char.IsAsciiDigit))
                {
                    report.ValidAccountIds++;
                }

                break;
            case "quantity" when reader.TokenType == JsonTokenType.Number:
                report.Quantities++;
                if (SignificantDigits(reader.ValueSpan) == 16)
                {
                    report.SixteenDigitQuantities++;
                }

                break;
            case "reference":
                report.References++;
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var reference) && reference > TwoPow53)
                {
                    report.ReferencesAbove2Pow53++;
                }

                break;
            default:
                break;
        }
    }

    private void Group(string groupId, long position)
    {
        if (currentGroup is null)
        {
            StartGroup(groupId);
        }
        else if (groupId == currentGroup)
        {
            currentGroupSize++;
        }
        else if (IsGreaterGroup(groupId, currentGroup))
        {
            EndGroup();
            StartGroup(groupId);
        }
        else
        {
            report.Fail($"Item {position} has groupId {groupId} after {currentGroup}: groups are not contiguous.");
        }
    }

    private void StartGroup(string groupId)
    {
        currentGroup = groupId;
        currentGroupSize = 1;
        report.Groups++;
    }

    private void EndGroup()
    {
        if (currentGroup is null)
        {
            return;
        }

        report.MinGroupSize = report.MinGroupSize == 0 ? currentGroupSize : Math.Min(report.MinGroupSize, currentGroupSize);
        report.MaxGroupSize = Math.Max(report.MaxGroupSize, currentGroupSize);
    }

    private void Description(string description)
    {
        if (description.Length >= 1_500)
        {
            report.LongDescriptions++;
        }

        if (Hostile.Contains(description))
        {
            report.HostileDescriptions++;
        }

        if (description.Any(static c => c > '\u007f'))
        {
            report.NonAsciiDescriptions++;
        }

        if (description.StartsWith("Ref-", StringComparison.Ordinal))
        {
            report.UniqueDescriptions++;
        }

        if (FormulaLike.Contains(description) && report.FormulaLikeByPrefix.ContainsKey(description[0]))
        {
            report.FormulaLikeByPrefix[description[0]]++;
        }
    }

    private void EndItem(long bytes)
    {
        report.Records++;
        report.MaxItemBytes = Math.Max(report.MaxItemBytes, bytes);
        if (bytes > MaxObjectBytes)
        {
            report.Fail($"Item {report.Records} is {bytes} bytes, above 1 MiB.");
        }

        if (!itemHasSequence)
        {
            report.Fail($"Item {report.Records} has no sequence.");
        }
    }

    private void Finish()
    {
        EndGroup();
        if (!rootStarted || !rootClosed)
        {
            report.Fail("The root object is missing or not closed.");
        }

        if (declaredRecordCount != report.Records)
        {
            report.Fail($"header.totals.recordCount is {declaredRecordCount?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, the file has {report.Records} items.");
        }

        if (declaredAmount != report.Amount)
        {
            report.Fail($"header.totals.amount is {declaredAmount?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, the items sum {report.Amount.ToString(CultureInfo.InvariantCulture)}.");
        }

        if ((declaredGroupCount ?? 0) != report.Groups)
        {
            report.Fail($"header.totals.groupCount is {declaredGroupCount?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, the file has {report.Groups} groups.");
        }
    }

    private static string Raw(ref Utf8JsonReader reader) =>
        System.Text.Encoding.UTF8.GetString(reader.ValueSpan);
}
