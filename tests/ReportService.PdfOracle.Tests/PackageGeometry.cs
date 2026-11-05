using System.Globalization;

namespace ReportService.PdfOracle.Tests;

/// <summary>
/// The geometry a test package declares in its own files: the columns its row
/// template prints and the order it prints them in, the width of each, the width of
/// the container they fill, and the margins its manifest gives the page. It is read
/// from the package, so the judge's declaration is held to the package's own figures
/// and a declaration that agrees only with itself is caught.
/// </summary>
internal sealed class PackageGeometry
{
    private PackageGeometry(
        IReadOnlyList<string> columns,
        IReadOnlyList<double> widthsMm,
        double containerWidthMm,
        double leftMarginMm,
        double rightMarginMm)
    {
        Columns = columns;
        WidthsMm = widthsMm;
        ContainerWidthMm = containerWidthMm;
        LeftMarginMm = leftMarginMm;
        RightMarginMm = rightMarginMm;

        var bands = new List<(double StartMm, double EndMm)>(columns.Count);
        var start = leftMarginMm;
        foreach (var width in widthsMm)
        {
            bands.Add((start, start + width));
            start += width;
        }

        Bands = bands;
    }

    /// <summary>The column names of the row template, in the order it prints them.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The width of each column, in the same order.</summary>
    public IReadOnlyList<double> WidthsMm { get; }

    /// <summary>The width of the table or the row the columns fill.</summary>
    public double ContainerWidthMm { get; }

    /// <summary>The left margin the manifest declares.</summary>
    public double LeftMarginMm { get; }

    /// <summary>The right margin the manifest declares.</summary>
    public double RightMarginMm { get; }

    /// <summary>Where each column's box lies, from the page's left edge, derived from the package's own widths.</summary>
    public IReadOnlyList<(double StartMm, double EndMm)> Bands { get; }

    /// <summary>Reads the package that lives in <paramref name="directory"/>.</summary>
    public static PackageGeometry Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var manifest = File.ReadAllText(Path.Combine(directory, "template.yaml"));
        var rules = Stylesheet.Read(File.ReadAllText(Path.Combine(directory, "html", "styles.css")));
        var columns = ColumnClasses(File.ReadAllText(Path.Combine(directory, "html", "row.html.sbn")));
        var container = Stylesheet.Value(rules, ["table.detail", "main.detail"], "width");
        List<double> widths;
        if (rules.TryGetValue(".row", out var row) && row.TryGetValue("grid-template-columns", out var template))
        {
            widths = [.. template.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Millimetres)];
        }
        else
        {
            widths = [.. columns.Select(column => Millimetres(Stylesheet.Value(rules, [".col-" + column], "width")))];
        }

        if (widths.Count != columns.Count)
        {
            throw new InvalidDataException($"The package declares {columns.Count} column(s) and {widths.Count} width(s).");
        }

        return new PackageGeometry(columns, widths, Millimetres(container), Manifest.Margin(manifest, "left"), Manifest.Margin(manifest, "right"));
    }

    /// <summary>The column classes of a row template, in the order the template prints them.</summary>
    public static IReadOnlyList<string> ColumnClasses(string rowTemplate)
    {
        ArgumentNullException.ThrowIfNull(rowTemplate);

        var columns = new List<string>();
        var position = 0;
        while (true)
        {
            var attribute = rowTemplate.IndexOf("class=\"", position, StringComparison.Ordinal);
            if (attribute < 0)
            {
                return columns;
            }

            var start = attribute + "class=\"".Length;
            var end = rowTemplate.IndexOf('"', start);
            if (end < 0)
            {
                throw new InvalidDataException("A class attribute of the row template is not closed.");
            }

            foreach (var name in rowTemplate[start..end].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (name.StartsWith("col-", StringComparison.Ordinal))
                {
                    columns.Add(name["col-".Length..]);
                }
            }

            position = end + 1;
        }
    }

    private static double Millimetres(string length)
    {
        if (!length.EndsWith("mm", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{length}' is not a length in millimetres.");
        }

        return double.Parse(length[..^"mm".Length], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>The rules of one stylesheet, by selector, refusing anything it does not understand.</summary>
    private static class Stylesheet
    {
        public static Dictionary<string, Dictionary<string, string>> Read(string css)
        {
            ArgumentNullException.ThrowIfNull(css);

            var rules = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var position = 0;
            while (position < css.Length)
            {
                while (position < css.Length && char.IsWhiteSpace(css[position]))
                {
                    position++;
                }

                if (position >= css.Length)
                {
                    break;
                }

                var open = css.IndexOf('{', position);
                var close = open < 0 ? -1 : css.IndexOf('}', open);
                if (open < 0 || close < 0)
                {
                    throw new InvalidDataException("The stylesheet holds a rule this reader cannot follow.");
                }

                var selector = css[position..open].Trim();
                var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var declaration in css[(open + 1)..close].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var colon = declaration.IndexOf(':');
                    if (colon < 0)
                    {
                        throw new InvalidDataException($"The rule '{selector}' holds '{declaration}', which is not a declaration.");
                    }

                    declarations[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
                }

                if (!rules.TryAdd(selector, declarations))
                {
                    throw new InvalidDataException($"The stylesheet declares '{selector}' twice.");
                }

                position = close + 1;
            }

            return rules;
        }

        /// <summary>The value of <paramref name="property"/> under the one of <paramref name="selectors"/> the sheet declares.</summary>
        public static string Value(Dictionary<string, Dictionary<string, string>> rules, IReadOnlyList<string> selectors, string property)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(selectors);

            var found = selectors.Where(rules.ContainsKey).ToList();
            if (found.Count != 1)
            {
                throw new InvalidDataException($"The stylesheet declares {found.Count} of '{string.Join("', '", selectors)}'.");
            }

            return rules[found[0]].TryGetValue(property, out var value)
                ? value
                : throw new InvalidDataException($"The rule '{found[0]}' declares no '{property}'.");
        }
    }

    /// <summary>The page the manifest declares: the paper's own width, not the declaration's.</summary>
    private static class Manifest
    {
        public static double Margin(string manifest, string side)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            var start = manifest.IndexOf("marginMm:", StringComparison.Ordinal);
            var open = start < 0 ? -1 : manifest.IndexOf('{', start);
            var close = open < 0 ? -1 : manifest.IndexOf('}', open);
            if (close < 0)
            {
                throw new InvalidDataException("The manifest declares no margins.");
            }

            foreach (var pair in manifest[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries))
            {
                var parts = pair.Split(':', StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && string.Equals(parts[0], side, StringComparison.Ordinal))
                {
                    return double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                }
            }

            throw new InvalidDataException($"The manifest declares no '{side}' margin.");
        }
    }
}
