using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReportService.TemplatePackages.Manifest;

/// <summary>
/// The engines a template version may declare. They are peers: none is a
/// default and none can be converted into another. The stored text of each is
/// the database's own vocabulary and never the manifest's.
/// </summary>
public enum TemplateEngine
{
    /// <summary>Scriban, the <c>.sbn</c> parts of the pilot packages.</summary>
    Scriban,

    /// <summary>Handlebars, the <c>.hbs</c> parts.</summary>
    Handlebars,

    /// <summary>Fluid (Liquid), the <c>.liquid</c> parts.</summary>
    LiquidFluid,
}

/// <summary>
/// The two vocabularies of an engine name: the manifest's lower-case word and
/// the stored upper-case text. Both are matched ordinally, never by culture or
/// by ignoring case, so <c>Razor</c>, <c>Scriban</c> and <c>scriban </c> are
/// each a value this deployment cannot render.
/// </summary>
public static class TemplateEngineNames
{
    /// <summary>The manifest name of <see cref="TemplateEngine.Scriban"/>.</summary>
    public const string Scriban = "scriban";

    /// <summary>The manifest name of <see cref="TemplateEngine.Handlebars"/>.</summary>
    public const string Handlebars = "handlebars";

    /// <summary>The manifest name of <see cref="TemplateEngine.LiquidFluid"/>.</summary>
    public const string Liquid = "liquid";

    /// <summary>Reads a manifest name; false when this deployment has no such engine.</summary>
    /// <param name="manifestName">The name as written in the manifest.</param>
    /// <param name="engine">The engine, when the name is one of the three.</param>
    public static bool TryParse(string? manifestName, [NotNullWhen(true)] out TemplateEngine? engine)
    {
        switch (manifestName)
        {
            case Scriban:
                engine = TemplateEngine.Scriban;
                return true;
            case Handlebars:
                engine = TemplateEngine.Handlebars;
                return true;
            case Liquid:
                engine = TemplateEngine.LiquidFluid;
                return true;
            default:
                engine = null;
                return false;
        }
    }

    /// <summary>The manifest name of <paramref name="engine"/>.</summary>
    /// <param name="engine">A declared engine.</param>
    public static string ToManifestName(TemplateEngine engine) => engine switch
    {
        TemplateEngine.Scriban => Scriban,
        TemplateEngine.Handlebars => Handlebars,
        TemplateEngine.LiquidFluid => Liquid,
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    /// <summary>The stored text of <paramref name="engine"/>, which the table's check accepts.</summary>
    /// <param name="engine">A declared engine.</param>
    public static string ToStoredText(TemplateEngine engine) => engine switch
    {
        TemplateEngine.Scriban => "SCRIBAN",
        TemplateEngine.Handlebars => "HANDLEBARS",
        TemplateEngine.LiquidFluid => "LIQUID_FLUID",
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    /// <summary>Reads a stored text; false when the row holds a value this build does not know.</summary>
    /// <param name="storedText">The text a row holds.</param>
    /// <param name="engine">The engine, when the text is one of the three.</param>
    public static bool TryParseStoredText(string? storedText, [NotNullWhen(true)] out TemplateEngine? engine)
    {
        switch (storedText)
        {
            case "SCRIBAN":
                engine = TemplateEngine.Scriban;
                return true;
            case "HANDLEBARS":
                engine = TemplateEngine.Handlebars;
                return true;
            case "LIQUID_FLUID":
                engine = TemplateEngine.LiquidFluid;
                return true;
            default:
                engine = null;
                return false;
        }
    }
}

/// <summary>
/// Reads and writes the engine of a parsed manifest. A name this deployment
/// cannot render is reported as a <see cref="JsonException"/> of its own, so a
/// caller can tell an unsupported engine apart from a malformed document.
/// </summary>
public sealed class TemplateEngineJsonConverter : JsonConverter<TemplateEngine>
{
    /// <inheritdoc />
    public override TemplateEngine Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("engine is not a string");
        }

        var name = reader.GetString();
        return TemplateEngineNames.TryParse(name, out var engine)
            ? engine.Value
            : throw new JsonException("engine is not one this deployment renders");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TemplateEngine value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(TemplateEngineNames.ToManifestName(value));
    }
}
