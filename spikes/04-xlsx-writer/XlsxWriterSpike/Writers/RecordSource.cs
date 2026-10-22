using XlsxWriterSpike.Input;
using XlsxWriterSpike.Typing;

namespace XlsxWriterSpike.Writers;

/// <summary>
/// Re-openable source of typed rows. Every enumeration of a sheet window reopens
/// the file and streams it again, so a writer that enumerates twice costs time,
/// never memory; the number of enumerations is reported.
/// </summary>
internal sealed class RecordSource(string path, ColumnProfile profile)
{
    private readonly Dictionary<string, long> _rowsPerSheet = new(StringComparer.Ordinal);

    /// <summary>The source file.</summary>
    public string Path { get; } = path;

    /// <summary>The column profile applied.</summary>
    public ColumnProfile Profile { get; } = profile;

    /// <summary>How many times a sheet window was enumerated.</summary>
    public int Enumerations { get; private set; }

    /// <summary>Rows yielded by the last enumeration of each sheet, summed.</summary>
    public long RowsYielded => _rowsPerSheet.Values.Sum();

    /// <summary>Reads only the header and returns <c>totals.recordCount</c>.</summary>
    public long? ReadHeaderCount()
    {
        using var reader = EnvelopeRecordReader.Open(Path, Profile, 0);
        return reader.RecordCount;
    }

    /// <summary>
    /// Typed rows of one sheet window. A record that would exceed
    /// <paramref name="rowLimit"/> rows in the sheet raises <c>OUTPUT_LIMIT_EXCEEDED</c>
    /// (the streaming guard).
    /// </summary>
    public IEnumerable<TypedRow> Rows(SheetWindow window, long rowLimit)
    {
        Enumerations++;
        _rowsPerSheet[window.Name] = 0;
        using var reader = EnvelopeRecordReader.Open(Path, Profile, window.Skip);
        var raw = new RawRecord(Profile.Columns.Count);
        var typer = new CellTyper(Profile);
        long count = 0;
        while (window.Take is not { } take || count < take)
        {
            if (!reader.TryReadNext(raw))
            {
                break;
            }

            count++;
            if (count > rowLimit)
            {
                throw new OutputLimitExceededException(
                    "rows", raw.RecordNumber, "-", "more than " + rowLimit.ToString(System.Globalization.CultureInfo.InvariantCulture) + " data rows in sheet " + window.Name);
            }

            _rowsPerSheet[window.Name] = count;
            yield return typer.Type(raw);
        }
    }
}
