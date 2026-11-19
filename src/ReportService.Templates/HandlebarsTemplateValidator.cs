using System.Text.RegularExpressions;
using HandlebarsDotNet;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.Templates;

/// <summary>
/// Parses a package's Handlebars parts by compiling them with an empty
/// configuration: no helper registered and no file system, so a compile is a parse
/// and nothing reaches the host. The library exposes no public syntax tree
/// (Handlebars.Net 2.4.3 keeps its lexer and parser internal), so the take's
/// include and lint checks read the file's text; this validator answers only
/// whether the engine's own parser accepts it.
/// </summary>
public sealed partial class HandlebarsTemplateValidator : ITemplateEngineValidator
{
    /// <inheritdoc />
    public TemplateEngine Engine => TemplateEngine.Handlebars;

    /// <inheritdoc />
    public string PartExtension => PartExtensions.Handlebars;

    /// <inheritdoc />
    public PartParseResult Parse(string packagePath, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        ArgumentNullException.ThrowIfNull(text);

        var handlebars = HandlebarsParsing.Create();
        try
        {
            _ = handlebars.Compile(text);
            return new PartParseResult.Parsed(new ParsedPart(text, text));
        }
        catch (HandlebarsException exception)
        {
            var position = Position().Match(exception.Message);
            return position.Success
                ? new PartParseResult.SyntaxError(
                    int.Parse(position.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(position.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
                : new PartParseResult.SyntaxError(null, null);
        }
    }

    /// <inheritdoc />
    public string? ResolveInclude(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return name.EndsWith(".hbs", StringComparison.Ordinal) ? name : name + ".hbs";
    }

    [GeneratedRegex(@"at:\s*(\d+):(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Position();
}
