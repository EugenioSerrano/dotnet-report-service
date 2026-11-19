using Scriban;
using ReportService.TemplatePackages.Manifest;

namespace ReportService.Templates;

/// <summary>
/// Parses a package's Scriban parts with the delivered adapter's own configuration
/// (<c>Template.Parse</c> with the default parser and lexer options, the same call
/// <see cref="ScribanTemplateAdapter"/> makes), so a file the take accepts is a
/// file the render can parse. It reads no file and renders nothing.
/// </summary>
public sealed class ScribanTemplateValidator : ITemplateEngineValidator
{
    /// <inheritdoc />
    public TemplateEngine Engine => TemplateEngine.Scriban;

    /// <inheritdoc />
    public string PartExtension => PartExtensions.Scriban;

    /// <inheritdoc />
    public PartParseResult Parse(string packagePath, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        ArgumentNullException.ThrowIfNull(text);

        var template = Template.Parse(text, packagePath, parserOptions: null, lexerOptions: null);
        if (!template.HasErrors)
        {
            return new PartParseResult.Parsed(new ParsedPart(template, text));
        }

        var first = template.Messages.Count > 0 ? template.Messages[0] : null;
        var line = first?.Span.Start.Line;
        var column = first?.Span.Start.Column;
        return new PartParseResult.SyntaxError(
            line is > 0 ? line : null,
            column is > 0 ? column : null);
    }

    /// <inheritdoc />
    public string? ResolveInclude(string name) => ScribanIncludes.Resolve(name);
}
