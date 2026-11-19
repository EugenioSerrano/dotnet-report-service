using System.Text.RegularExpressions;
using Fluid;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.Templates;

/// <summary>
/// Parses a package's Liquid parts with Fluid's own parser and its default
/// options, the configuration the render half will use. It reads no file, loads no
/// partial and renders nothing.
/// </summary>
public sealed partial class FluidTemplateValidator : ITemplateEngineValidator
{
    /// <inheritdoc />
    public TemplateEngine Engine => TemplateEngine.LiquidFluid;

    /// <inheritdoc />
    public string PartExtension => PartExtensions.Liquid;

    /// <inheritdoc />
    public PartParseResult Parse(string packagePath, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        ArgumentNullException.ThrowIfNull(text);

        var parser = FluidParsing.Create();
        if (parser.TryParse(text, out var template, out var error))
        {
            return new PartParseResult.Parsed(new ParsedPart(template, text));
        }

        var position = Position().Match(error ?? string.Empty);
        return position.Success
            ? new PartParseResult.SyntaxError(
                int.Parse(position.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(position.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            : new PartParseResult.SyntaxError(null, null);
    }

    /// <inheritdoc />
    public string? ResolveInclude(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return name.EndsWith(".liquid", StringComparison.Ordinal) ? name : name + ".liquid";
    }

    [GeneratedRegex(@"\((\d+):(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Position();
}
