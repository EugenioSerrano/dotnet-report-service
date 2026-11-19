using Fluid;

namespace ReportService.Templates;

/// <summary>
/// The one Liquid parser the product makes: Fluid's own parser with its default options,
/// used by the take's validator and by the render's adapter, so the same text is accepted
/// in both halves.
/// </summary>
internal static class FluidParsing
{
    /// <summary>Creates the parser, and nothing else.</summary>
    public static FluidParser Create() => new();
}
