using System.Text.Json;

namespace XlsxWriterSpike.Typing;

/// <summary>The declared type of one output column.</summary>
internal enum ColumnType
{
    /// <summary>Whole number written as a numeric cell.</summary>
    Integer,

    /// <summary>Decimal number written as a numeric cell.</summary>
    Decimal,

    /// <summary>Text written as a text cell, whatever the JSON token was.</summary>
    String,
}

/// <summary>One output column: the record property it reads and its declared type.</summary>
/// <param name="Name">Record property name, also the header text.</param>
/// <param name="Type">Declared type.</param>
internal sealed record ColumnDefinition(string Name, ColumnType Type);

/// <summary>The columns of the sheet, in output order, and the sheet name.</summary>
internal sealed class ColumnProfile
{
    /// <summary>Creates a profile.</summary>
    public ColumnProfile(string name, string sheetName, IReadOnlyList<ColumnDefinition> columns)
    {
        if (columns.Count == 0)
        {
            throw new SpikeException("A column profile needs at least one column.");
        }

        if (columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != columns.Count)
        {
            throw new SpikeException("A column profile lists a column twice.");
        }

        Name = name;
        SheetName = sheetName;
        Columns = columns;
    }

    /// <summary>Profile name (file name without extension).</summary>
    public string Name { get; }

    /// <summary>Name of the first sheet.</summary>
    public string SheetName { get; }

    /// <summary>Columns in output order.</summary>
    public IReadOnlyList<ColumnDefinition> Columns { get; }

    /// <summary>Loads a profile file: <c>{ "sheetName": ..., "columns": [ { "name", "type" } ] }</c>.</summary>
    public static ColumnProfile Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var sheetName = root.TryGetProperty("sheetName", out var sheet) ? sheet.GetString() ?? "items" : "items";
        if (!root.TryGetProperty("columns", out var columnsElement) || columnsElement.ValueKind != JsonValueKind.Array)
        {
            throw new SpikeException($"Profile '{Path.GetFileName(path)}' has no 'columns' array.");
        }

        var columns = new List<ColumnDefinition>();
        foreach (var column in columnsElement.EnumerateArray())
        {
            var name = column.GetProperty("name").GetString()
                ?? throw new SpikeException($"Profile '{Path.GetFileName(path)}' has a column without a name.");
            var typeText = column.GetProperty("type").GetString();
            var type = typeText switch
            {
                "integer" => ColumnType.Integer,
                "decimal" => ColumnType.Decimal,
                "string" => ColumnType.String,
                _ => throw new SpikeException(
                    $"Profile '{Path.GetFileName(path)}', column '{name}': type '{typeText}' is not supported (integer, decimal or string)."),
            };
            columns.Add(new ColumnDefinition(name, type));
        }

        return new ColumnProfile(Path.GetFileNameWithoutExtension(path), sheetName, columns);
    }
}
