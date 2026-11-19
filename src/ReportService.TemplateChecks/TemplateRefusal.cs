using System.Globalization;
using System.Text;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks;

/// <summary>
/// The one place a refusal's text is built: a package-relative file, what is
/// wrong, and what to fix, in at most 1 024 bytes of UTF-8. It echoes a name only
/// when the name is printable ASCII of at most 128 characters, and of a URL only
/// its scheme, so a message never carries template text, a value or a host path.
/// </summary>
public static class TemplateRefusal
{
    /// <summary>The most bytes one refusal's reason may occupy.</summary>
    public const int MaximumBytes = 1_024;

    /// <summary>The most characters a name the message echoes may occupy.</summary>
    public const int MaximumEchoedNameCharacters = 128;

    /// <summary>The rule identifiers carried to the log and the counter, never to the author's sentence.</summary>
    public static class Rules
    {
        /// <summary>The manifest declares something this service does not deliver.</summary>
        public const string FeatureNotDelivered = "feature-not-delivered";

        /// <summary>A page setting is missing or unusable.</summary>
        public const string PageSettingsInvalid = "page-settings-invalid";

        /// <summary>A manifest path is outside the package or names no file.</summary>
        public const string ManifestPath = "manifest-path";

        /// <summary>The schema is outside the template profile.</summary>
        public const string SchemaKeyword = "schema-keyword";

        /// <summary>The schema cannot be compiled.</summary>
        public const string SchemaCompile = "schema-compile";

        /// <summary>A pattern repeats a part that repeats without limit.</summary>
        public const string SchemaPattern = "schema-pattern";

        /// <summary>The sample does not follow the data format.</summary>
        public const string SampleFormat = "sample-format";

        /// <summary>The sample breaks its schema.</summary>
        public const string SampleViolation = "sample-violation";

        /// <summary>The sample could not be decided in time.</summary>
        public const string SampleDeadline = "sample-deadline";

        /// <summary>An anchor names a property the schema does not declare.</summary>
        public const string AnchorUndeclared = "anchor-undeclared";

        /// <summary>A part or an include does not parse.</summary>
        public const string PartSyntax = "part-syntax";

        /// <summary>A file is not UTF-8 text.</summary>
        public const string PartEncoding = "part-encoding";

        /// <summary>A declared part carries another engine's extension.</summary>
        public const string PartExtension = "part-extension";

        /// <summary>An include is not a fixed name.</summary>
        public const string IncludeNotLiteral = "include-not-literal";

        /// <summary>An include or asset points outside the package.</summary>
        public const string Outside = "outside";

        /// <summary>An include names no file the render loads.</summary>
        public const string IncludeMissing = "include-missing";

        /// <summary>An include chain repeats a file.</summary>
        public const string IncludeCycle = "include-cycle";

        /// <summary>An asset names no file the package holds.</summary>
        public const string AssetMissing = "asset-missing";

        /// <summary>An output of a data value is not escaped.</summary>
        public const string LintUnescapedOutput = "lint-unescaped-output";

        /// <summary>A raw output is applied.</summary>
        public const string LintRawOutput = "lint-raw-output";

        /// <summary>A forbidden function, filter or helper is called.</summary>
        public const string LintForbiddenFunction = "lint-forbidden-function";

        /// <summary>Data is written in a context that must not carry it.</summary>
        public const string LintDataContext = "lint-data-context";

        /// <summary>A Liquid range is not fixed or is too large.</summary>
        public const string LintRange = "lint-range";

        /// <summary>The declared engine version is not this deployment's major.</summary>
        public const string EngineVersion = "engine-version-mismatch";

        /// <summary>The checks ran out of their share of the take's deadline.</summary>
        public const string Budget = "check-budget";
    }

    /// <summary>Refuses a version with the message form the author reads.</summary>
    /// <param name="code">One of <see cref="TemplateCheckCodes"/>.</param>
    /// <param name="file">The package-relative file the refusal is about.</param>
    /// <param name="problem">What is wrong.</param>
    /// <param name="fix">What the author should do.</param>
    public static TemplateCheckResult Refuse(string code, string file, string problem, string fix)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentException.ThrowIfNullOrEmpty(problem);
        ArgumentException.ThrowIfNullOrEmpty(fix);

        var reason = Truncate(file + ": " + problem + " " + fix);
        return TemplateCheckResult.Refuse(code, reason);
    }

    /// <summary>
    /// The text a message may echo: printable ASCII of at most 128 characters,
    /// else "(name not shown)".
    /// </summary>
    /// <param name="value">The name a message would carry.</param>
    public static string Echo(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumEchoedNameCharacters)
        {
            return "(name not shown)";
        }

        foreach (var character in value)
        {
            if (character is < ' ' or > '~')
            {
                return "(name not shown)";
            }
        }

        return value;
    }

    /// <summary>
    /// The scheme of a URL as a message may show it: its lower-cased RFC 3986
    /// scheme and a colon when the text starts with one, else "scheme not shown".
    /// </summary>
    /// <param name="url">The URL the template writes.</param>
    public static string Scheme(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "scheme not shown";
        }

        var colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon is <= 0 or > 16)
        {
            return "scheme not shown";
        }

        var scheme = url[..colon];
        foreach (var character in scheme)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '+' or '-' or '.'))
            {
                return "scheme not shown";
            }
        }

        return scheme.ToLowerInvariant() + ":";
    }

    private static string Truncate(string reason)
    {
        if (Encoding.UTF8.GetByteCount(reason) <= MaximumBytes)
        {
            return reason;
        }

        var builder = new StringBuilder(reason.Length);
        var bytes = 0;
        foreach (var character in reason)
        {
            var size = Encoding.UTF8.GetByteCount(char.ToString(character));
            if (bytes + size > MaximumBytes - 4)
            {
                break;
            }

            _ = builder.Append(character);
            bytes += size;
        }

        return builder.Append('…').ToString();
    }

    /// <summary>The invariant text of a number a message carries.</summary>
    /// <param name="value">The number.</param>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
