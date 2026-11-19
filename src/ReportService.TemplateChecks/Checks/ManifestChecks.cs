using System.Text.Json;
using ReportService.Data;
using ReportService.DataContract;
using ReportService.TemplateChecks.Scanning;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Checks;

/// <summary>
/// Check 1: the manifest's own declarations — the data profile, the parts, every
/// path it names, the features this increment does not deliver, and the PDF page
/// settings through the definition shared with printing. It is the cheapest check
/// and runs first, because every later check trusts the paths it confirms.
/// </summary>
public sealed class ManifestAndPathsCheck(TemplateCheckSession session) : ITemplateVersionCheck
{
    private readonly TemplateCheckSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <inheritdoc />
    public string Name => "ManifestAndPathsCheck";

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
        var view = _session.View(candidate);
        using var document = JsonDocument.Parse(candidate.ManifestJson);
        var root = document.RootElement;

        var refusal = Profile(manifest)
            ?? Parts(manifest)
            ?? Paths(manifest, view)
            ?? Features(root)
            ?? PageSettings(root);
        return Task.FromResult(refusal ?? TemplateCheckResult.Pass());
    }

    private static TemplateCheckResult? Profile(TemplateManifest manifest)
    {
        var contract = manifest.DataContract;
        if (contract is null)
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                PackagePath.ManifestFileName,
                "the data contract is missing.",
                "Declare dataContract.profile header-items-v1, version 1.0 and the schema's path.");
        }

        if (contract.Profile is null)
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                PackagePath.ManifestFileName,
                "dataContract.profile is missing.",
                "Declare the profile header-items-v1.");
        }

        if (!string.Equals(contract.Profile, "header-items-v1", StringComparison.Ordinal))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.UnsupportedCapability,
                PackagePath.ManifestFileName,
                "the data profile \"" + TemplateRefusal.Echo(contract.Profile) + "\" is not supported.",
                "Use header-items-v1.");
        }

        if (!string.Equals(contract.Version, "1.0", StringComparison.Ordinal))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                PackagePath.ManifestFileName,
                "dataContract.version must be \"1.0\".",
                "Declare version 1.0.");
        }

        if (contract.MaxDepth is { } depth && depth is < 1 or > 64)
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                PackagePath.ManifestFileName,
                "dataContract.maxDepth must be an integer from 1 to 64.",
                "Declare a depth the profile admits, or remove the member.");
        }

        return null;
    }

    private static TemplateCheckResult? Parts(TemplateManifest manifest)
    {
        var html = manifest.Outputs?.Html?.Parts;
        if (html?.DocumentStart is null || html.Row is null || html.DocumentEnd is null)
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                PackagePath.ManifestFileName,
                "outputs.html.parts does not declare documentStart, row and documentEnd.",
                "Declare all three parts; groupStart and groupEnd are optional.");
        }

        var pdf = manifest.Outputs?.Pdf?.Parts;
        if (pdf is not null && (pdf.DocumentStart is null || pdf.Row is null || pdf.DocumentEnd is null))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                PackagePath.ManifestFileName,
                "outputs.pdf.parts does not declare documentStart, row and documentEnd.",
                "Declare all three parts, or remove outputs.pdf.parts to use the HTML parts.");
        }

        return null;
    }

    private static TemplateCheckResult? Paths(TemplateManifest manifest, PackageView view)
    {
        foreach (var (field, path) in CheckSupport.Parts(manifest))
        {
            if (RefusePath(field, path, view) is { } refusal)
            {
                return refusal;
            }
        }

        foreach (var style in CheckSupport.Styles(manifest))
        {
            if (RefusePath("outputs.html.styles", style, view) is { } refusal)
            {
                return refusal;
            }
        }

        var schema = manifest.DataContract?.Schema;
        return schema is null ? null : RefusePath("dataContract.schema", schema, view);
    }

    private static TemplateCheckResult? RefusePath(string field, string path, PackageView view)
    {
        if (!PackagePath.IsValid(path))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                PackagePath.ManifestFileName,
                field + " names \"" + TemplateRefusal.Echo(path) + "\", which is outside the package.",
                "Name a package-relative path with '/' separators.");
        }

        return view.Exists(path)
            ? null
            : TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                PackagePath.ManifestFileName,
                field + " names \"" + TemplateRefusal.Echo(path) + "\", which does not exist in the package.",
                "Add the file, or correct the path.");
    }

    private static TemplateCheckResult? Features(JsonElement root)
    {
        var fields = new (string Field, bool Declared)[]
        {
            ("aggregates", Declared(root, "aggregates", null)),
            ("dataContract.verifiedTotals", Declared(root, "dataContract", "verifiedTotals")),
            ("partitioning.groupOrder", Declared(root, "partitioning", "groupOrder")),
        };

        foreach (var (field, declared) in fields)
        {
            if (declared)
            {
                return TemplateRefusal.Refuse(
                    TemplateCheckCodes.UnsupportedCapability,
                    PackagePath.ManifestFileName,
                    "the manifest declares \"" + field + "\", which this service does not deliver yet.",
                    "Remove the declaration until the feature is offered.");
            }
        }

        return null;
    }

    private static bool Declared(JsonElement root, string member, string? nested)
    {
        if (!root.TryGetProperty(member, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (nested is null)
        {
            return true;
        }

        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(nested, out var inner)
            && inner.ValueKind != JsonValueKind.Null;
    }

    private static TemplateCheckResult? PageSettings(JsonElement root)
    {
        if (!root.TryGetProperty("outputs", out var outputs)
            || outputs.ValueKind != JsonValueKind.Object
            || !outputs.TryGetProperty("pdf", out var pdf))
        {
            return null;
        }

        var result = PdfPageSettingsRules.Resolve(pdf);
        if (result.Resolved)
        {
            return null;
        }

        var field = result.UnusableField ?? PdfPageSettingsRules.MarginField;
        var fix = field switch
        {
            PdfPageSettingsRules.PaperField => "Use A4 or Letter.",
            PdfPageSettingsRules.OrientationField => "Use portrait or landscape.",
            _ => "Declare all four margins as numbers in millimetres leaving at least 50 mm of printable width and height.",
        };

        return TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            PackagePath.ManifestFileName,
            field + " is missing or not usable.",
            fix);
    }
}

/// <summary>
/// Check 2: <c>schema.json</c> under the template profile, compiled once for the
/// sample and anchor checks. A catastrophic pattern is refused before the schema
/// is compiled, so a hostile document never reaches the evaluator.
/// </summary>
public sealed class SchemaCheck(TemplateCheckSession session) : ITemplateVersionCheck
{
    private readonly TemplateCheckSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <inheritdoc />
    public string Name => "SchemaCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (_session.BudgetExceeded(candidate))
        {
            return Task.FromResult(CheckSupport.BudgetRefusal());
        }

        var view = _session.View(candidate);
        var schema = candidate.Manifest.DataContract?.Schema;
        if (schema is null || !view.Exists(schema))
        {
            return Task.FromResult(TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                "schema.json",
                "the schema the manifest names is missing from the package.",
                "Add the file, or correct dataContract.schema."));
        }

        var bytes = view.ReadBytes(schema);
        if (bytes.Length > DataContractProfile.MaximumBytes)
        {
            return Task.FromResult(TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                "schema.json",
                "the schema is larger than the profile allows (rule size).",
                "Reduce the schema below 64 KiB."));
        }

        if (SchemaPatternRule.Find(bytes) is { } pattern)
        {
            return Task.FromResult(TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                "schema.json",
                "the pattern at " + TemplateRefusal.Echo(pattern.Pointer) + " repeats a part that itself repeats without limit and may take too long.",
                "Rewrite it without nested repetition."));
        }

        var compilation = DataContractCompiler.Compile(bytes, candidate.SourceDescriptorSha256, schema);
        if (!compilation.Profile.IsValid)
        {
            var rule = compilation.Profile.Rule ?? "schema";
            var pointer = compilation.Profile.SchemaPointer ?? string.Empty;
            return Task.FromResult(TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                "schema.json",
                "the schema is not inside the template profile (rule " + TemplateRefusal.Echo(rule) + " at " + TemplateRefusal.Echo(pointer) + ").",
                "Correct the schema to the profile; the authoring guide lists the admitted keywords and formats."));
        }

        if (compilation.Contract is null)
        {
            return Task.FromResult(TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                "schema.json",
                "the schema cannot be compiled (rule compile).",
                "Correct the schema."));
        }

        _session.KeepContract(candidate, compilation.Contract);
        return Task.FromResult(TemplateCheckResult.Pass());
    }
}

/// <summary>
/// Check 3: <c>sample.json</c> — the document profile's own reader, then the whole
/// sample against the schema's root, inside a deadline that a schema which cannot
/// be decided fails closed on.
/// </summary>
public sealed class SampleCheck(TemplateCheckSession session, TimeProvider clock) : ITemplateVersionCheck
{
    /// <summary>The deadline of the whole sample check.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly TemplateCheckSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <inheritdoc />
    public string Name => "SampleCheck";

    /// <inheritdoc />
    public async Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (_session.BudgetExceeded(candidate))
        {
            return CheckSupport.BudgetRefusal();
        }

        var view = _session.View(candidate);
        if (!view.Exists("sample.json"))
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                "sample.json",
                "the sample is missing.",
                "Add the sample the take renders.");
        }

        var bytes = view.ReadBytes("sample.json");
        try
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            await foreach (var _ in HeaderItemsReader.ReadAsync(stream, cancellationToken))
            {
            }
        }
        catch (DataContractViolation violation)
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                "sample.json",
                "the sample does not follow the data format (" + TemplateRefusal.Echo(violation.Reason ?? violation.Code) + ", record " + TemplateRefusal.Number(violation.Ordinal) + ").",
                "Correct the sample.");
        }

        var contract = _session.Contract(candidate);
        if (contract is null)
        {
            return TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                "schema.json",
                "the schema could not be prepared for the sample.",
                "Correct the schema.");
        }

        var started = _clock.GetTimestamp();
        ContractCheck check;
        try
        {
            check = contract.CheckEnvelope(bytes);
        }
        catch (ContractEvaluationFailed failure)
        {
            return DeadlineRefusal(failure.Kind);
        }

        if (_clock.GetElapsedTime(started) >= Deadline)
        {
            return DeadlineRefusal("deadline");
        }

        return check.IsValid
            ? TemplateCheckResult.Pass()
            : TemplateRefusal.Refuse(
                TemplateCheckCodes.TemplateInvalid,
                "sample.json",
                "the record at " + TemplateRefusal.Echo(check.Path) + " does not satisfy the schema (rule \"" + TemplateRefusal.Echo(check.Keyword) + "\").",
                "Correct the sample or the schema.");
    }

    private static TemplateCheckResult DeadlineRefusal(string kind) =>
        TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            "sample.json",
            "could not be checked against schema.json within the time limit (" + TemplateRefusal.Echo(kind) + ").",
            "Simplify the schema's patterns or nesting.");
}

/// <summary>
/// Check 4: every anchor path a PDF package declares names a property the schema
/// declares at the header or the record root, following <c>properties</c> through
/// <c>$ref</c> and the combinators.
/// </summary>
public sealed class AnchorCheck(TemplateCheckSession session) : ITemplateVersionCheck
{
    private readonly TemplateCheckSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <inheritdoc />
    public string Name => "AnchorCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (_session.BudgetExceeded(candidate))
        {
            return Task.FromResult(CheckSupport.BudgetRefusal());
        }

        var anchors = candidate.Manifest.Outputs?.Pdf?.Verification?.Anchors;
        if (anchors is null)
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var view = _session.View(candidate);
        var schema = candidate.Manifest.DataContract?.Schema;
        if (schema is null || !view.Exists(schema))
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        using var document = CheckSupport.ParseSchema(view.ReadBytes(schema));
        if (document is null)
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var root = document.RootElement;
        var header = Resolve(root, "#/properties/header");
        var record = Resolve(root, "#/properties/items/items");

        foreach (var path in anchors.Header ?? [])
        {
            if (header is { } node && !IsDeclared(node, root, Segments(path)))
            {
                return Task.FromResult(Refusal(path, "header", "the header"));
            }
        }

        foreach (var path in anchors.Item ?? [])
        {
            if (record is { } node && !IsDeclared(node, root, Segments(path)))
            {
                return Task.FromResult(Refusal(path, "item", "the record"));
            }
        }

        return Task.FromResult(TemplateCheckResult.Pass());
    }

    private static TemplateCheckResult Refusal(string path, string list, string root) =>
        TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            PackagePath.ManifestFileName,
            "the anchor \"" + TemplateRefusal.Echo(path) + "\" in outputs.pdf.verification.anchors." + list + " is not a property of " + root + " in schema.json.",
            "Declare it in the schema or remove the anchor.");

    private static IReadOnlyList<string> Segments(string path)
    {
        var trimmed = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path;
        return [.. trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static bool IsDeclared(JsonElement node, JsonElement root, IReadOnlyList<string> segments, int index = 0)
    {
        if (index >= segments.Count)
        {
            return true;
        }

        return TryProperty(node, root, segments[index], out var next)
            && IsDeclared(next, root, segments, index + 1);
    }

    private static bool TryProperty(JsonElement node, JsonElement root, string name, out JsonElement next)
    {
        next = default;
        if (node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (node.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(name, out next))
        {
            return true;
        }

        foreach (var combinator in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (node.TryGetProperty(combinator, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (TryProperty(item, root, name, out next))
                    {
                        return true;
                    }
                }
            }
        }

        foreach (var branch in new[] { "then", "else" })
        {
            if (node.TryGetProperty(branch, out var item) && TryProperty(item, root, name, out next))
            {
                return true;
            }
        }

        if (node.TryGetProperty("dependentSchemas", out var dependent) && dependent.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in dependent.EnumerateObject())
            {
                if (TryProperty(item.Value, root, name, out next))
                {
                    return true;
                }
            }
        }

        if (node.TryGetProperty("$ref", out var reference)
            && reference.ValueKind == JsonValueKind.String
            && Resolve(root, reference.GetString()) is { } target)
        {
            return TryProperty(target, root, name, out next);
        }

        return false;
    }

    private static JsonElement? Resolve(JsonElement root, string? pointer)
    {
        if (pointer is null || !pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        var current = root;
        foreach (var segment in pointer[2..].Split('/'))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current;
    }
}
