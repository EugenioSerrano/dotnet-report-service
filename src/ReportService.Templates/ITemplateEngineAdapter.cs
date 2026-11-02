namespace ReportService.Templates;

/// <summary>
/// What a prepared package holds: the manifest it was prepared for, the parsed
/// form of each declared part and the part's source text. An instance is shared
/// between renders and is never mutated by one, so no state travels from one
/// block, or one record, to the next.
/// </summary>
/// <param name="Manifest">The manifest the package was prepared for.</param>
/// <param name="parts">The parsed part and its source, by part.</param>
public sealed class PreparedTemplate(RenderManifest manifest, IReadOnlyDictionary<TemplatePart, PreparedPart> parts)
{
    /// <summary>The manifest the package was prepared for.</summary>
    public RenderManifest Manifest { get; } = manifest;

    /// <summary>The engine's parsed representation of a part.</summary>
    public object Part(TemplatePart part) => Find(part).Parsed;

    /// <summary>The part's source text, as the package holds it.</summary>
    public string Source(TemplatePart part) => Find(part).Source;

    /// <summary>Whether the package declares <paramref name="part"/>.</summary>
    public bool Has(TemplatePart part) => parts.ContainsKey(part);

    private PreparedPart Find(TemplatePart part) =>
        parts.TryGetValue(part, out var prepared)
            ? prepared
            : throw new TemplatePackageException($"The package declares no {TemplatePartNames.Of(part)} part.");
}

/// <summary>One declared part: what the engine parsed, and the text it was parsed from.</summary>
/// <param name="Parsed">The engine's own parsed representation.</param>
/// <param name="Source">The text the package holds for the part.</param>
public sealed record PreparedPart(object Parsed, string Source);

/// <summary>
/// The one way the product reaches a template engine: prepare a package once,
/// then render one part at a time into a guarded writer. Every engine is reached
/// through this interface, and nothing outside an adapter references an engine
/// package.
/// </summary>
public interface ITemplateEngineAdapter
{
    /// <summary>The engine this adapter composes, as a manifest names it.</summary>
    string Engine { get; }

    /// <summary>Parses the parts the manifest declares; an unparsable part is refused here.</summary>
    PreparedTemplate Prepare(RenderManifest manifest, ITemplatePackageFiles files);

    /// <summary>Renders one part into <paramref name="writer"/> with the values of <paramref name="context"/>.</summary>
    void RenderPart(
        PreparedTemplate template,
        TemplatePart part,
        RenderContext context,
        GuardedWriter writer,
        CancellationToken cancellationToken);
}
