using System.Globalization;
using System.Text;
using ReportService.TemplateChecks.Scanning;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.Templates;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Checks;

/// <summary>
/// Check 5: every file the render may load parses with its engine's own parser —
/// the declared parts, every Handlebars partial, and every include reached by a
/// literal name. The engine's message is never shown or logged.
/// </summary>
public sealed class SyntaxCheck(TemplateCheckSession session) : ITemplateVersionCheck
{
    private readonly TemplateCheckSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <inheritdoc />
    public string Name => "SyntaxCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (_session.BudgetExceeded(candidate))
        {
            return Task.FromResult(CheckSupport.BudgetRefusal());
        }

        var manifest = candidate.Manifest;
        var engine = manifest.Engine!.Value;
        var view = _session.View(candidate);
        var declared = CheckSupport.Parts(manifest).ToDictionary(entry => entry.Value, entry => entry.Key, StringComparer.Ordinal);
        var validator = Validators.For(engine);

        foreach (var path in ReachableFiles.Files(view, manifest, engine))
        {
            if (declared.ContainsKey(path) && !path.EndsWith(validator.PartExtension, StringComparison.Ordinal))
            {
                return Task.FromResult(TemplateRefusal.Refuse(
                    TemplateCheckCodes.TemplateInvalid,
                    TemplateRefusal.Echo(path),
                    "is declared for " + EngineName(engine) + ", whose parts end in " + validator.PartExtension + ".",
                    "Rename the file or change the engine."));
            }

            string text;
            try
            {
                text = view.ReadText(path);
            }
            catch (DecoderFallbackException)
            {
                return Task.FromResult(TemplateRefusal.Refuse(
                    TemplateCheckCodes.TemplateInvalid,
                    TemplateRefusal.Echo(path),
                    "is not valid UTF-8 text.",
                    "Save the file as UTF-8."));
            }

            if (validator.Parse(path, text) is PartParseResult.SyntaxError error)
            {
                var where = error.Line is { } line && error.Column is { } column
                    ? " at line " + line.ToString(CultureInfo.InvariantCulture) + ", column " + column.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
                return Task.FromResult(TemplateRefusal.Refuse(
                    TemplateCheckCodes.TemplateInvalid,
                    TemplateRefusal.Echo(path),
                    "syntax error" + where + " (" + EngineName(engine) + ").",
                    "Correct the template syntax."));
            }
        }

        return Task.FromResult(TemplateCheckResult.Pass());
    }

    internal static string EngineName(TemplateEngine engine) => engine switch
    {
        TemplateEngine.Scriban => "Scriban",
        TemplateEngine.Handlebars => "Handlebars",
        _ => "Liquid",
    };
}

/// <summary>The engine validators, built per check run.</summary>
internal static class Validators
{
    internal static ITemplateEngineValidator For(TemplateEngine engine) => engine switch
    {
        TemplateEngine.Scriban => new ScribanTemplateValidator(),
        TemplateEngine.Handlebars => new HandlebarsTemplateValidator(),
        _ => new FluidTemplateValidator(),
    };
}

/// <summary>
/// The files one check reads: the declared parts, every Handlebars partial (the
/// render registers them all), and the includes a literal name reaches, in that
/// order, once each.
/// </summary>
internal static class ReachableFiles
{
    internal const int MaximumDepth = 16;

    internal static IReadOnlyList<string> Files(PackageView view, TemplateManifest manifest, TemplateEngine engine)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var declared = CheckSupport.PartPaths(manifest);

        foreach (var path in declared)
        {
            if (seen.Add(path))
            {
                ordered.Add(path);
            }
        }

        if (engine == TemplateEngine.Handlebars)
        {
            foreach (var path in view.Files.Order(StringComparer.Ordinal))
            {
                if (path.EndsWith(PartExtensions.Handlebars, StringComparison.Ordinal)
                    && !declared.Contains(path, StringComparer.Ordinal)
                    && seen.Add(path))
                {
                    ordered.Add(path);
                }
            }
        }

        var dialect = CheckSupport.Dialect(engine);
        var validator = Validators.For(engine);
        var queue = new Queue<(string Path, int Depth)>(ordered.Select(path => (path, 0)));
        while (queue.Count > 0)
        {
            var (path, depth) = queue.Dequeue();
            if (depth >= MaximumDepth || !view.Exists(path))
            {
                continue;
            }

            string text;
            try
            {
                text = view.ReadText(path);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            foreach (var include in TemplateSourceScan.Scan(text, dialect).Includes)
            {
                if (!include.IsLiteral)
                {
                    continue;
                }

                var resolved = validator.ResolveInclude(include.Name);
                if (resolved is not null && view.Exists(resolved) && seen.Add(resolved))
                {
                    ordered.Add(resolved);
                    queue.Enqueue((resolved, depth + 1));
                }
            }
        }

        return ordered;
    }
}

/// <summary>
/// Check 6: every include and partial resolves to a file the render loads, inside
/// the package, with its engine's extension and without a cycle; and every
/// reference a part or a stylesheet makes the render fetch names a file the package
/// holds, or is refused before anything is kept.
/// </summary>
public sealed class IncludeAndAssetCheck(TemplateCheckSession session) : ITemplateVersionCheck
{
    private readonly TemplateCheckSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <inheritdoc />
    public string Name => "IncludeAndAssetCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (_session.BudgetExceeded(candidate))
        {
            return Task.FromResult(CheckSupport.BudgetRefusal());
        }

        var manifest = candidate.Manifest;
        var engine = manifest.Engine!.Value;
        var view = _session.View(candidate);
        var declared = CheckSupport.PartPaths(manifest).ToHashSet(StringComparer.Ordinal);
        var validator = Validators.For(engine);
        var dialect = CheckSupport.Dialect(engine);
        var files = ReachableFiles.Files(view, manifest, engine);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var path in files)
        {
            string text;
            try
            {
                text = view.ReadText(path);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            var scan = TemplateSourceScan.Scan(text, dialect);
            foreach (var include in scan.Includes)
            {
                if (!include.IsLiteral || !PackagePath.IsValid(include.Name))
                {
                    return Task.FromResult(TemplateRefusal.Refuse(
                        TemplateCheckCodes.TemplateInvalid,
                        TemplateRefusal.Echo(path),
                        include.IsLiteral
                            ? "the include at line " + Line(include.Line) + " points outside the package."
                            : "the include at line " + Line(include.Line) + " is not a fixed name.",
                        include.IsLiteral ? "Name a package-relative path with '/' separators." : "Write the file's path as text."));
                }

                var resolved = validator.ResolveInclude(include.Name);
                if (resolved is null)
                {
                    return Task.FromResult(TemplateRefusal.Refuse(
                        TemplateCheckCodes.TemplateInvalid,
                        TemplateRefusal.Echo(path),
                        "the include \"" + TemplateRefusal.Echo(include.Name) + "\" at line " + Line(include.Line) + " is not a file the render loads.",
                        "Name a declared part (or a package partial) and check its path."));
                }

                if (engine == TemplateEngine.Scriban && !declared.Contains(resolved))
                {
                    return Task.FromResult(TemplateRefusal.Refuse(
                        TemplateCheckCodes.TemplateInvalid,
                        TemplateRefusal.Echo(path),
                        "the include \"" + TemplateRefusal.Echo(include.Name) + "\" at line " + Line(include.Line) + " is not a part this render loads.",
                        "Include a declared part, or move the shared markup into one."));
                }

                if (engine == TemplateEngine.Handlebars && declared.Contains(resolved))
                {
                    return Task.FromResult(TemplateRefusal.Refuse(
                        TemplateCheckCodes.TemplateInvalid,
                        TemplateRefusal.Echo(path),
                        "the partial \"" + TemplateRefusal.Echo(include.Name) + "\" at line " + Line(include.Line) + " names a declared part, which is not a partial.",
                        "Move the shared markup into its own .hbs file."));
                }

                if (!view.Exists(resolved))
                {
                    return Task.FromResult(TemplateRefusal.Refuse(
                        TemplateCheckCodes.TemplateInvalid,
                        TemplateRefusal.Echo(path),
                        "the include \"" + TemplateRefusal.Echo(include.Name) + "\" at line " + Line(include.Line) + " does not exist in the package.",
                        "Add the file or correct the name."));
                }

                if (!resolved.EndsWith(validator.PartExtension, StringComparison.Ordinal))
                {
                    return Task.FromResult(TemplateRefusal.Refuse(
                        TemplateCheckCodes.TemplateInvalid,
                        TemplateRefusal.Echo(path),
                        "the include \"" + TemplateRefusal.Echo(include.Name) + "\" at line " + Line(include.Line) + " is not a " + SyntaxCheck.EngineName(engine) + " file.",
                        "Include a file of the engine's own extension."));
                }

                if (!edges.TryGetValue(path, out var targets))
                {
                    targets = [];
                    edges[path] = targets;
                }

                targets.Add(resolved);
            }
        }

        foreach (var cycle in Cycles(edges, files))
        {
            return Task.FromResult(TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                TemplateRefusal.Echo(cycle[0]),
                "includes itself through " + string.Join(" → ", cycle.Select(TemplateRefusal.Echo)) + ".",
                "Remove the cycle."));
        }

        foreach (var asset in Assets(view, manifest))
        {
            if (RefuseAsset(asset, view) is { } refusal)
            {
                return Task.FromResult(refusal);
            }
        }

        return Task.FromResult(TemplateCheckResult.Pass());
    }

    private static IEnumerable<IReadOnlyList<string>> Cycles(Dictionary<string, List<string>> edges, IReadOnlyList<string> files)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        foreach (var file in files)
        {
            if (Walk(file, edges, state, stack) is { } cycle)
            {
                yield return cycle;
            }
        }

        static IReadOnlyList<string>? Walk(string path, Dictionary<string, List<string>> edges, Dictionary<string, int> state, List<string> stack)
        {
            if (state.TryGetValue(path, out var visited))
            {
                return visited == 1 ? [.. stack, path] : null;
            }

            state[path] = 1;
            stack.Add(path);
            if (edges.TryGetValue(path, out var targets))
            {
                foreach (var target in targets)
                {
                    if (Walk(target, edges, state, stack) is { } cycle)
                    {
                        return cycle;
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[path] = 2;
            return null;
        }
    }

    private static IEnumerable<AssetReference> Assets(PackageView view, TemplateManifest manifest)
    {
        var dialect = CheckSupport.Dialect(manifest.Engine!.Value);
        foreach (var path in CheckSupport.PartPaths(manifest))
        {
            if (!view.Exists(path))
            {
                continue;
            }

            string text;
            try
            {
                text = view.ReadText(path);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            var regions = TemplateSourceScan.Scan(text, dialect).Regions;
            foreach (var asset in HtmlScanner.Assets(text, regions))
            {
                yield return asset with { Kind = path + ":" + asset.Kind };
            }
        }

        foreach (var style in CheckSupport.Styles(manifest))
        {
            if (!view.Exists(style))
            {
                continue;
            }

            string text;
            try
            {
                text = view.ReadText(style);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            foreach (var url in CssScanner.Urls(text))
            {
                yield return new AssetReference(url, 0, style, InCss: true, Image: true);
            }
        }
    }

    private static TemplateCheckResult? RefuseAsset(AssetReference asset, PackageView view)
    {
        var url = asset.Url;
        var file = asset.Kind.Contains(':', StringComparison.Ordinal) ? asset.Kind[..asset.Kind.IndexOf(':', StringComparison.Ordinal)] : asset.Kind;

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return asset.Image
                ? null
                : TemplateRefusal.Refuse(
                    TemplateCheckCodes.TemplateInvalid,
                    TemplateRefusal.Echo(file),
                    "points outside the package (a data: URL is not admitted here).",
                    "Put the file in the package and refer to it by its path.");
        }

        var scheme = url.IndexOf(':', StringComparison.Ordinal);
        if ((scheme > 0 && url[..scheme].All(character => char.IsLetterOrDigit(character) || character is '+' or '-' or '.'))
            || url.StartsWith("//", StringComparison.Ordinal)
            || url.StartsWith('/'))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                TemplateRefusal.Echo(file),
                "points outside the package (an absolute URL, " + TemplateRefusal.Scheme(url) + ").",
                "Put the file in the package and refer to it by its path.");
        }

        var path = url.Split('#')[0].Split('?')[0];
        path = Uri.UnescapeDataString(path);
        if (asset.InCss)
        {
            var basePath = asset.Kind.Contains(':', StringComparison.Ordinal) ? asset.Kind[..asset.Kind.IndexOf(':', StringComparison.Ordinal)] : asset.Kind;
            var directory = basePath.Contains('/', StringComparison.Ordinal) ? basePath[..basePath.LastIndexOf('/')] : string.Empty;
            path = directory.Length == 0 ? path : directory + "/" + path;
        }

        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.StartsWith("../", StringComparison.Ordinal) || path.Contains("/../", StringComparison.Ordinal))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                TemplateRefusal.Echo(file),
                "points outside the package.",
                "Put the file in the package and refer to it by its path.");
        }

        if (!PackagePath.IsValid(path))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                TemplateRefusal.Echo(file),
                "points outside the package.",
                "Put the file in the package and refer to it by its path.");
        }

        return view.Exists(path)
            ? null
            : TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                TemplateRefusal.Echo(file),
                "the file \"" + TemplateRefusal.Echo(path) + "\" does not exist in the package.",
                "Add the file, or correct the reference.");
    }

    private static string Line(int line) => line.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Check 7: the escaping and sandbox lint of ADR-022 §3.9–§3.10 — a data output
/// that is not escaped, a raw output, a forbidden function, data in a context that
/// must not carry it, and a Liquid range that is not fixed or is too large.
/// </summary>
public sealed class TemplateLintCheck(TemplateCheckSession session) : ITemplateVersionCheck
{
    private readonly TemplateCheckSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <inheritdoc />
    public string Name => "TemplateLintCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (_session.BudgetExceeded(candidate))
        {
            return Task.FromResult(CheckSupport.BudgetRefusal());
        }

        var manifest = candidate.Manifest;
        var engine = manifest.Engine!.Value;
        var view = _session.View(candidate);
        var dialect = CheckSupport.Dialect(engine);

        foreach (var path in ReachableFiles.Files(view, manifest, engine))
        {
            string text;
            try
            {
                text = view.ReadText(path);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            var scan = TemplateSourceScan.Scan(text, dialect);
            foreach (var output in scan.Outputs)
            {
                var where = " at line " + output.Line.ToString(CultureInfo.InvariantCulture) + ", column " + output.Column.ToString(CultureInfo.InvariantCulture);

                if (output.IsRaw)
                {
                    return Task.FromResult(TemplateRefusal.Refuse(
                        TemplateCheckCodes.TemplateInvalid,
                        TemplateRefusal.Echo(path),
                        "the raw output" + where + " writes a value without the service's encoding.",
                        "Use {{ }} — values are encoded by the service."));
                }

                if (output.IsData && !output.IsEscaped)
                {
                    return Task.FromResult(TemplateRefusal.Refuse(
                        TemplateCheckCodes.TemplateInvalid,
                        TemplateRefusal.Echo(path),
                        "the output" + where + " writes a data value without escaping.",
                        engine == TemplateEngine.Scriban
                            ? "End the output with | html.escape."
                            : "Remove the raw filter — values are encoded by the service."));
                }

                if (output.IsData)
                {
                    var context = HtmlScanner.ContextAt(text, output.Offset, scan.Regions);
                    var dangerous = context.Kind switch
                    {
                        HtmlContextKind.Script => "<script element>",
                        HtmlContextKind.Style => "<style element>",
                        HtmlContextKind.Tag => "a tag, where an attribute name is expected",
                        HtmlContextKind.AttributeValueUnquoted => "an unquoted attribute value",
                        HtmlContextKind.AttributeValueQuoted
                            when HtmlScanner.IsDangerousAttribute(context.AttributeName)
                                && !HtmlScanner.IsImageSourcePosition(context.TagName, context.AttributeName) =>
                            "the \"" + context.AttributeName + "\" attribute",
                        _ => null,
                    };

                    if (dangerous is not null)
                    {
                        return Task.FromResult(TemplateRefusal.Refuse(
                            TemplateCheckCodes.TemplateInvalid,
                            TemplateRefusal.Echo(path),
                            "the value" + where + " is written inside " + dangerous + ".",
                            "Data may only appear in element text, quoted attribute values and the quoted source of an image, which must be an image data URI when the report is rendered."));
                    }
                }
            }

            foreach (var call in scan.Calls)
            {
                return Task.FromResult(TemplateRefusal.Refuse(
                    TemplateCheckCodes.TemplateInvalid,
                    TemplateRefusal.Echo(path),
                    "\"" + TemplateRefusal.Echo(call.Name) + "\" is not available in templates.",
                    "Remove the call."));
            }

            foreach (var range in scan.Ranges)
            {
                if (!range.FromIsInteger || !range.ToIsInteger)
                {
                    return Task.FromResult(TemplateRefusal.Refuse(
                        TemplateCheckCodes.TemplateInvalid,
                        TemplateRefusal.Echo(path),
                        "the range at line " + range.Line.ToString(CultureInfo.InvariantCulture) + " must have fixed integer bounds.",
                        "Write both bounds as numbers."));
                }

                if (range.Count is { } count && count > 10_000)
                {
                    return Task.FromResult(TemplateRefusal.Refuse(
                        TemplateCheckCodes.TemplateInvalid,
                        TemplateRefusal.Echo(path),
                        "the range at line " + range.Line.ToString(CultureInfo.InvariantCulture) + " has more than 10 000 values.",
                        "Reduce the range."));
                }
            }
        }

        return Task.FromResult(TemplateCheckResult.Pass());
    }
}

/// <summary>
/// Check 8: a declared <c>engineVersion</c> names this deployment's major for its
/// engine, or the version asks for a renderer this build does not run.
/// </summary>
public sealed class EngineVersionCheck : ITemplateVersionCheck
{
    /// <inheritdoc />
    public string Name => "EngineVersionCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var engine = candidate.Manifest.Engine!.Value;
        var declared = candidate.Manifest.DeclaredEngineVersion;
        if (string.IsNullOrEmpty(declared))
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var deployed = TemplateEngineCatalog.VersionOf(engine);
        var parts = declared.Split('.');
        var valid = parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _);
        var deployedMajor = deployed.Split('.')[0];

        if (valid && string.Equals(parts[0], deployedMajor, StringComparison.Ordinal))
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        return Task.FromResult(TemplateRefusal.Refuse(
            TemplateCheckCodes.UnsupportedCapability,
            PackagePath.ManifestFileName,
            "the template was written for " + SyntaxCheck.EngineName(engine) + " " + TemplateRefusal.Echo(declared) + "; this service runs " + SyntaxCheck.EngineName(engine) + " " + deployed + ".",
            "Declare a " + deployedMajor + ".x version or rewrite the template for it."));
    }
}
