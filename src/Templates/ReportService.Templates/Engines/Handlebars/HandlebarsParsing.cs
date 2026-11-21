using HandlebarsDotNet;

namespace ReportService.Templates;

/// <summary>
/// The one Handlebars configuration the product makes: an empty one, with no helper of
/// the product and no file system, so the take's compile stays a parse and nothing
/// reaches the host. The render half adds its own encoder, helpers and partials to the
/// instance this returns, and never to the factory.
/// </summary>
internal static class HandlebarsParsing
{
    /// <summary>Creates the parse configuration, and nothing else.</summary>
    public static IHandlebars Create() => Handlebars.Create(new HandlebarsConfiguration());
}
