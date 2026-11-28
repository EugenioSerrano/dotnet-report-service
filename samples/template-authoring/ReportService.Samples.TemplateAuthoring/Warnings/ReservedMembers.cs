namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// The members an engine answers itself instead of reading the object's field of that name,
/// at the engine versions the service pins. Only a member that shadows a field silently is
/// listed: Liquid's <c>size</c> returns the number of an object's members. In Scriban and
/// Handlebars every probed name returns the field (Scriban's <c>this</c> fails the render
/// instead, which the author sees).
/// </summary>
public static class ReservedMembers
{
    /// <summary>The engine whose members shadow fields.</summary>
    public const string LiquidEngine = "liquid";

    /// <summary>The shadowing members of <paramref name="engine"/>, or none.</summary>
    /// <param name="engine">The engine's manifest name.</param>
    public static IReadOnlyList<string> Of(string engine) =>
        string.Equals(engine, LiquidEngine, StringComparison.Ordinal) ? ["size"] : [];
}
