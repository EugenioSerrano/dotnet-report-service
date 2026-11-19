using ReportService.TemplatePackages.Manifest;

namespace ReportService.Templates;

/// <summary>
/// What parsing one template file answered: the engine's own parsed form, or the
/// place a syntax error starts at. A refusal carries a position only when the
/// engine gives one; it never carries the engine's message, which quotes the
/// template.
/// </summary>
public abstract record PartParseResult
{
    private PartParseResult()
    {
    }

    /// <summary>The file parsed.</summary>
    /// <param name="part">The parsed file and the text it was parsed from.</param>
    public sealed record Parsed(ParsedPart Part) : PartParseResult;

    /// <summary>The file does not parse.</summary>
    /// <param name="line">The line the error starts at, when the engine gives one.</param>
    /// <param name="column">The column the error starts at, when the engine gives one.</param>
    public sealed record SyntaxError(int? Line, int? Column) : PartParseResult;
}

/// <summary>One parsed file: the engine's own representation and the text it came from.</summary>
/// <param name="Ast">The engine's parsed form. Only the engine's validator reads it.</param>
/// <param name="Source">The file's text, as the package holds it.</param>
public sealed record ParsedPart(object Ast, string Source);

/// <summary>
/// The one way the take asks an engine whether a file parses: the engine's own
/// parser, with the configuration the render uses, inside the adapter assembly
/// (ADR-022 §3.7 item 2). Nothing here renders, loads a partial or touches a file.
/// </summary>
public interface ITemplateEngineValidator
{
    /// <summary>The engine this validator parses for.</summary>
    TemplateEngine Engine { get; }

    /// <summary>The extension a part of this engine carries.</summary>
    string PartExtension { get; }

    /// <summary>Parses one file.</summary>
    /// <param name="packagePath">The package-relative path, used only as a name in diagnostics.</param>
    /// <param name="text">The file's text.</param>
    PartParseResult Parse(string packagePath, string text);

    /// <summary>
    /// The package path the render loads for an include or partial name, or
    /// <see langword="null"/> when the render would load nothing for it. The
    /// spelling is the engine's: a Scriban name as written, a Handlebars partial
    /// plus <c>.hbs</c>, a Liquid name plus <c>.liquid</c> when absent.
    /// </summary>
    /// <param name="name">The include or partial name, as the file writes it.</param>
    string? ResolveInclude(string name);
}

/// <summary>The part extensions ADR-022 §3.7 fixes, in one place.</summary>
public static class PartExtensions
{
    /// <summary>Scriban's extension.</summary>
    public const string Scriban = ".sbn";

    /// <summary>Handlebars's extension.</summary>
    public const string Handlebars = ".hbs";

    /// <summary>Liquid's extension.</summary>
    public const string Liquid = ".liquid";
}
