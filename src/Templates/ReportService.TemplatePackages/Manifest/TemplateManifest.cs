using System.Text.Json.Serialization;

namespace ReportService.TemplatePackages.Manifest;

/// <summary>
/// The typed view of a template package's manifest: the members this build
/// reads, deserialised from the stored JSON. The stored document keeps every
/// member it had, so a later build types more of them without a version being
/// taken again; the members below that are absent from a document are
/// <see langword="null"/> and are refused by the take where they are required.
/// </summary>
public sealed record TemplateManifest
{
    /// <summary>The manifest format, which must be the string <c>1.0</c>.</summary>
    [JsonPropertyName("manifestVersion")]
    public string? ManifestVersion { get; init; }

    /// <summary>The template's identifier, which must equal the folder's name.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The version, which must equal the folder's name.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>The consumer that owns the version.</summary>
    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    /// <summary>Whether every consumer may use the taken version; absent means false.</summary>
    [JsonPropertyName("shared")]
    public bool Shared { get; init; }

    /// <summary>The declared engine, refused when this deployment cannot render it.</summary>
    [JsonPropertyName("engine")]
    [JsonConverter(typeof(TemplateEngineJsonConverter))]
    public TemplateEngine? Engine { get; init; }

    /// <summary>The engine version as written; kept, never compared with the deployment's pin here.</summary>
    [JsonPropertyName("engineVersion")]
    public string? DeclaredEngineVersion { get; init; }

    /// <summary>The data contract the package is written against.</summary>
    [JsonPropertyName("dataContract")]
    public TemplateDataContract? DataContract { get; init; }

    /// <summary>The documents the package can produce.</summary>
    [JsonPropertyName("outputs")]
    public TemplateOutputs? Outputs { get; init; }

    /// <summary>The grouping of the records, when the package groups them.</summary>
    [JsonPropertyName("partitioning")]
    public TemplatePartitioning? Partitioning { get; init; }

    /// <summary>The aggregates the package reports; empty when absent.</summary>
    [JsonPropertyName("aggregates")]
    public IReadOnlyList<TemplateAggregate> Aggregates
    {
        get => _aggregates;
        init => _aggregates = value ?? [];
    }

    /// <summary>The culture and time zone the package prefers.</summary>
    [JsonPropertyName("localeDefaults")]
    public TemplateLocaleDefaults? LocaleDefaults { get; init; }

    private readonly IReadOnlyList<TemplateAggregate> _aggregates = [];
}

/// <summary>The data contract a package is written against.</summary>
public sealed record TemplateDataContract
{
    /// <summary>The profile of the records.</summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    /// <summary>The profile's version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>The package-relative path of the schema.</summary>
    [JsonPropertyName("schema")]
    public string? Schema { get; init; }

    /// <summary>The nesting depth the schema admits.</summary>
    [JsonPropertyName("maxDepth")]
    public int? MaxDepth { get; init; }

    /// <summary>Whether the records declare their totals.</summary>
    [JsonPropertyName("verifiedTotals")]
    public bool? VerifiedTotals { get; init; }
}

/// <summary>The documents a package can produce.</summary>
public sealed record TemplateOutputs
{
    /// <summary>The HTML document.</summary>
    [JsonPropertyName("html")]
    public TemplateHtmlOutput? Html { get; init; }

    /// <summary>The PDF document.</summary>
    [JsonPropertyName("pdf")]
    public TemplatePdfOutput? Pdf { get; init; }

    /// <summary>The XLSX workbook.</summary>
    [JsonPropertyName("xlsx")]
    public TemplateXlsxOutput? Xlsx { get; init; }
}

/// <summary>The HTML document of a package.</summary>
public sealed record TemplateHtmlOutput
{
    /// <summary>The package-relative paths of the document's parts.</summary>
    [JsonPropertyName("parts")]
    public TemplateParts? Parts { get; init; }

    /// <summary>The package-relative paths of the stylesheets.</summary>
    [JsonPropertyName("styles")]
    public IReadOnlyList<string> Styles
    {
        get => _styles;
        init => _styles = value ?? [];
    }

    private readonly IReadOnlyList<string> _styles = [];
}

/// <summary>The package-relative paths of one document's parts.</summary>
public sealed record TemplateParts
{
    /// <summary>The part that opens the document.</summary>
    [JsonPropertyName("documentStart")]
    public string? DocumentStart { get; init; }

    /// <summary>The part that opens a group.</summary>
    [JsonPropertyName("groupStart")]
    public string? GroupStart { get; init; }

    /// <summary>The part printed once per record.</summary>
    [JsonPropertyName("row")]
    public string? Row { get; init; }

    /// <summary>The part that closes a group.</summary>
    [JsonPropertyName("groupEnd")]
    public string? GroupEnd { get; init; }

    /// <summary>The part that closes the document.</summary>
    [JsonPropertyName("documentEnd")]
    public string? DocumentEnd { get; init; }
}

/// <summary>The PDF document of a package.</summary>
public sealed record TemplatePdfOutput
{
    /// <summary>The paper size.</summary>
    [JsonPropertyName("paper")]
    public string? Paper { get; init; }

    /// <summary>The page orientation.</summary>
    [JsonPropertyName("orientation")]
    public string? Orientation { get; init; }

    /// <summary>The page margins, in millimetres.</summary>
    [JsonPropertyName("marginMm")]
    public TemplateMargins? MarginMm { get; init; }

    /// <summary>Where a page may break.</summary>
    [JsonPropertyName("chunkLayout")]
    public string? ChunkLayout { get; init; }

    /// <summary>How the pages are numbered.</summary>
    [JsonPropertyName("pageNumbering")]
    public string? PageNumbering { get; init; }

    /// <summary>The printed totals.</summary>
    [JsonPropertyName("pageTotals")]
    public TemplatePageTotals? PageTotals { get; init; }

    /// <summary>The parts of the PDF document, when it is rendered from parts.</summary>
    [JsonPropertyName("parts")]
    public TemplateParts? Parts { get; init; }

    /// <summary>The verification the printed document must pass.</summary>
    [JsonPropertyName("verification")]
    public TemplatePdfVerification? Verification { get; init; }
}

/// <summary>The page margins of a PDF document, in millimetres.</summary>
public sealed record TemplateMargins
{
    /// <summary>The top margin.</summary>
    [JsonPropertyName("top")]
    public decimal? Top { get; init; }

    /// <summary>The right margin.</summary>
    [JsonPropertyName("right")]
    public decimal? Right { get; init; }

    /// <summary>The bottom margin.</summary>
    [JsonPropertyName("bottom")]
    public decimal? Bottom { get; init; }

    /// <summary>The left margin.</summary>
    [JsonPropertyName("left")]
    public decimal? Left { get; init; }
}

/// <summary>The printed totals of a PDF document.</summary>
public sealed record TemplatePageTotals
{
    /// <summary>Whether the totals are printed.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>The field that carries the amount, when one does.</summary>
    [JsonPropertyName("amountField")]
    public string? AmountField { get; init; }

    /// <summary>The scale the printed amounts use.</summary>
    [JsonPropertyName("scale")]
    public decimal? Scale { get; init; }
}

/// <summary>The verification a printed PDF document must pass.</summary>
public sealed record TemplatePdfVerification
{
    /// <summary>The field paths the printed header and rows must carry.</summary>
    [JsonPropertyName("anchors")]
    public TemplatePdfAnchors? Anchors { get; init; }
}

/// <summary>The field paths the printed PDF must carry, as the package declares them.</summary>
public sealed record TemplatePdfAnchors
{
    /// <summary>The field paths printed in the header.</summary>
    [JsonPropertyName("header")]
    public IReadOnlyList<string>? Header { get; init; }

    /// <summary>The field paths printed in every row.</summary>
    [JsonPropertyName("item")]
    public IReadOnlyList<string>? Item { get; init; }
}

/// <summary>The XLSX workbook of a package.</summary>
public sealed record TemplateXlsxOutput
{
    /// <summary>What a value outside the declared columns is written as.</summary>
    [JsonPropertyName("fallback")]
    public string? Fallback { get; init; }

    /// <summary>The columns, in the order the workbook has them.</summary>
    [JsonPropertyName("columns")]
    public IReadOnlyList<TemplateXlsxColumn>? Columns { get; init; }
}

/// <summary>One column of a workbook.</summary>
public sealed record TemplateXlsxColumn
{
    /// <summary>The field the column prints.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The type the column prints the field as.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

/// <summary>How a package groups the records.</summary>
public sealed record TemplatePartitioning
{
    /// <summary>The field the records are grouped by.</summary>
    [JsonPropertyName("groupKey")]
    public string? GroupKey { get; init; }

    /// <summary>The order of the groups.</summary>
    [JsonPropertyName("groupOrder")]
    public string? GroupOrder { get; init; }

    /// <summary>Where a group may be cut.</summary>
    [JsonPropertyName("preferredCut")]
    public string? PreferredCut { get; init; }
}

/// <summary>One aggregate a package reports.</summary>
public sealed record TemplateAggregate
{
    /// <summary>The aggregate's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The field it aggregates.</summary>
    [JsonPropertyName("field")]
    public string? Field { get; init; }

    /// <summary>The operation it applies.</summary>
    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

    /// <summary>The scope it applies over.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

/// <summary>The culture and time zone a package prefers.</summary>
public sealed record TemplateLocaleDefaults
{
    /// <summary>The culture name.</summary>
    [JsonPropertyName("culture")]
    public string? Culture { get; init; }

    /// <summary>The time zone name.</summary>
    [JsonPropertyName("timeZone")]
    public string? TimeZone { get; init; }
}
