using System.Globalization;
using System.Text;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// One authoring warning: a trap the tool can see in a package that rendered. A warning is
/// advice for the author; it never changes the exit code or the files the render writes.
/// </summary>
/// <param name="Code">The stable kebab-case name of the trap.</param>
/// <param name="Order">Where the warning sorts: the source file, line and column, or the output position.</param>
/// <param name="Fields">The ordered <c>name=value</c> pairs the line prints.</param>
/// <param name="Remedy">The one-phrase remedy printed after the fields.</param>
public sealed record AuthoringWarning(string Code, WarningOrder Order, IReadOnlyList<KeyValuePair<string, string>> Fields, string Remedy)
{
    /// <summary>The longest value a warning prints; a longer one is cut and marked.</summary>
    public const int MaximumValueLength = 32;

    /// <summary>The line the tool prints.</summary>
    public string ToLine()
    {
        var line = new StringBuilder("warning: ").Append(Code);
        foreach (var (name, value) in Fields)
        {
            line.Append(' ').Append(name).Append('=').Append(Printable(value));
        }

        return line.Append(" - ").Append(Remedy).ToString();
    }

    /// <summary>
    /// The value as one printable line: control characters, line breaks included, written as
    /// escapes, then cut to <see cref="MaximumValueLength"/> characters.
    /// </summary>
    /// <param name="value">The value as found.</param>
    public static string Printable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            _ = character switch
            {
                '\n' => escaped.Append("\\n"),
                '\r' => escaped.Append("\\r"),
                '\t' => escaped.Append("\\t"),
                _ when char.IsControl(character) || character is '\u2028' or '\u2029' or '\u0085' =>
                    escaped.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)),
                _ => escaped.Append(character),
            };
        }

        if (escaped.Length <= MaximumValueLength)
        {
            return escaped.ToString();
        }

        var cut = MaximumValueLength - 1;
        if (char.IsHighSurrogate(escaped[cut - 1]))
        {
            cut--;
        }

        return escaped.ToString(0, cut) + "\u2026";
    }
}

/// <summary>Where a warning sorts: source warnings first, by file, line and column; output warnings after, by position.</summary>
/// <param name="Kind">0 for a source warning, 1 for an output warning.</param>
/// <param name="File">The package-relative file, or empty for an output warning.</param>
/// <param name="Line">The 1-based line, or the output byte offset.</param>
/// <param name="Column">The 1-based column, or 0.</param>
public sealed record WarningOrder(int Kind, string File, long Line, int Column) : IComparable<WarningOrder>
{
    /// <inheritdoc />
    public int CompareTo(WarningOrder? other)
    {
        if (other is null)
        {
            return 1;
        }

        var kind = Kind.CompareTo(other.Kind);
        if (kind != 0)
        {
            return kind;
        }

        var file = string.CompareOrdinal(File, other.File);
        if (file != 0)
        {
            return file;
        }

        var line = Line.CompareTo(other.Line);
        return line != 0 ? line : Column.CompareTo(other.Column);
    }
}
