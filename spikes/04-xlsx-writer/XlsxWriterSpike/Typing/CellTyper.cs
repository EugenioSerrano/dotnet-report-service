using System.Globalization;
using System.Text.Json;
using XlsxWriterSpike.Input;

namespace XlsxWriterSpike.Typing;

/// <summary>
/// The per-column typing rule. The column profile decides the cell type; a
/// number that the spreadsheet cannot hold exactly (more than 15 significant
/// digits, or a value the decimal type cannot represent exactly) fails with
/// <c>OUTPUT_LIMIT_EXCEEDED</c> and is never rounded; a <c>string</c> column is
/// written as text exactly as it appears in the source; no cell is ever a formula.
/// </summary>
internal sealed class CellTyper(ColumnProfile profile)
{
    /// <summary>Largest number of significant digits a numeric cell may carry.</summary>
    public const int MaxSignificantDigits = 15;

    /// <summary>Largest number of characters a cell may carry.</summary>
    public const int MaxCellLength = 32_767;

    private readonly TypedRow _row = new(profile.Columns.Count);

    /// <summary>Profile the typer applies.</summary>
    public ColumnProfile Profile { get; } = profile;

    /// <summary>Types one raw record; the returned row is reused by the next call.</summary>
    public TypedRow Type(RawRecord record)
    {
        _row.RecordNumber = record.RecordNumber;
        for (var i = 0; i < Profile.Columns.Count; i++)
        {
            _row.Cells[i] = TypeCell(Profile.Columns[i], record.Values[i], record.RecordNumber);
        }

        return _row;
    }

    /// <summary>Types one value of a column.</summary>
    public static TypedCell TypeCell(ColumnDefinition column, RawValue raw, long row)
    {
        if (raw.Token == JsonTokenType.Null)
        {
            return new TypedCell { Kind = CellKind.Empty };
        }

        switch (column.Type)
        {
            case ColumnType.String:
                var text = raw.Token switch
                {
                    JsonTokenType.String or JsonTokenType.Number => raw.Text!,
                    JsonTokenType.True => "true",
                    JsonTokenType.False => "false",
                    _ => throw Mismatch(column, raw, row),
                };
                if (text.Length > MaxCellLength)
                {
                    throw new OutputLimitExceededException("cell-length", row, column.Name, text);
                }

                return new TypedCell { Kind = CellKind.Text, Text = text };

            case ColumnType.Integer:
                if (raw.Token != JsonTokenType.Number || raw.Text!.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
                {
                    throw Mismatch(column, raw, row);
                }

                return Number(column, raw.Text!, NumberStyles.AllowLeadingSign, row);

            case ColumnType.Decimal:
                if (raw.Token != JsonTokenType.Number)
                {
                    throw Mismatch(column, raw, row);
                }

                return Number(column, raw.Text!, NumberStyles.Float, row);

            default:
                throw new SpikeException($"Unknown column type {column.Type}.");
        }
    }

    /// <summary>
    /// Counts significant digits on the raw token text (never on a parsed value),
    /// parses it, and checks that the parsed decimal has exactly the value of the
    /// token: underflow to zero, rounding of more than 28 decimals or overflow of
    /// the decimal range are all <c>OUTPUT_LIMIT_EXCEEDED</c>.
    /// </summary>
    private static TypedCell Number(ColumnDefinition column, string raw, NumberStyles styles, long row)
    {
        if (!NumberText.TryParse(raw, out var source))
        {
            throw new SpikeException($"Record {row}, column '{column.Name}': '{raw}' is not a number.");
        }

        if (source.SignificantDigits > MaxSignificantDigits)
        {
            throw new OutputLimitExceededException("precision", row, column.Name, raw);
        }

        if (!decimal.TryParse(raw, styles, CultureInfo.InvariantCulture, out var value))
        {
            throw new OutputLimitExceededException("precision", row, column.Name, raw);
        }

        if (value == 0m)
        {
            // Keeps the scale and drops a negative sign: -0 is written as 0.
            value = new decimal(0, 0, 0, isNegative: false, value.Scale);
        }

        var written = value.ToString(CultureInfo.InvariantCulture);
        if (!NumberText.TryParse(written, out var parsed) || parsed != source)
        {
            throw new OutputLimitExceededException("precision", row, column.Name, raw);
        }

        return new TypedCell { Kind = CellKind.Number, Number = value, Text = written };
    }

    private static SpikeException Mismatch(ColumnDefinition column, RawValue raw, long row) =>
        new($"Record {row}, column '{column.Name}': a {raw.Token} value '{raw.Text}' does not match the declared type '{column.Type.ToString().ToLowerInvariant()}'.");
}
