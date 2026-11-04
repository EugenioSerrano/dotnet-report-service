using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplateSource.Manifest;

/// <summary>
/// What reading one <c>template.yaml</c> answered: the document mapped to its
/// canonical JSON — and, when this build can type it, its typed view — or the
/// one refusal every malformed or unsupported manifest produces. A refusal is a
/// value, never an exception: the input is the template author's, and the same
/// bytes are refused the same way on every attempt.
/// </summary>
public abstract record ManifestReadResult
{
    private protected ManifestReadResult()
    {
    }

    /// <summary>True when the document was read.</summary>
    public bool Succeeded => this is ReadManifest;

    /// <summary>The canonical JSON of a document that was read; null when it was refused.</summary>
    public string? CanonicalJson => (this as ReadManifest)?.Json;

    /// <summary>The typed view of a document that was read and types, or null when it does not type.</summary>
    public TemplateManifest? Manifest => (this as ReadManifest)?.Typed;

    /// <summary>What an author has to fix; null when the document was read.</summary>
    public string? Reason => (this as RefusedManifest)?.Text;

    /// <summary>The document was read.</summary>
    /// <param name="json">Its canonical JSON.</param>
    /// <param name="typed">Its typed view, or <see langword="null"/> when this build cannot type it.</param>
    public static ManifestReadResult Read(string json, TemplateManifest? typed)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        return new ReadManifest(json, typed);
    }

    /// <summary>The document was refused, naming the file, a line, a column and the rule.</summary>
    /// <param name="reason">What an author has to fix.</param>
    public static ManifestReadResult Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new RefusedManifest(reason);
    }
}

/// <summary>A manifest that was read: its canonical JSON and, when this build types it, its typed view.</summary>
/// <param name="Json">The stored form of the document.</param>
/// <param name="Typed">The typed view of the members this build reads, or null when it cannot be built.</param>
public sealed record ReadManifest(string Json, TemplateManifest? Typed) : ManifestReadResult;

/// <summary>A manifest that was refused: what the author has to fix.</summary>
/// <param name="Text">The refusal, naming <c>template.yaml</c>, a line, a column and the rule.</param>
public sealed record RefusedManifest(string Text) : ManifestReadResult;
