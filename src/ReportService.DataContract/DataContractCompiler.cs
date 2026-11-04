using Corvus.Text.Json.RuntimeEvaluator;

namespace ReportService.DataContract;

/// <summary>
/// Compiles a template's data contract: the profile is checked first, then the schema is
/// loaded once with the fixed options — the 2020-12 dialect, a base address inside the
/// reserved <c>report.invalid</c> host, no document resolver and no fallback resolver, the
/// nine formats asserted, a one-second pattern limit, the depth ceiling, and the regular
/// expressions compiled once. The two roots are taken from the same loaded document, so a
/// worker prepares a package's contract once and evaluates every record against it.
/// </summary>
public static class DataContractCompiler
{
    private const int MaximumDepth = 64;

    private static readonly TimeSpan DefaultPatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The library the contract is compiled with, at the version pinned for the product.</summary>
    public static string LibraryVersion { get; } = DescribeLibraryVersion();

    /// <summary>Compiles a schema document under the profile and the fixed options.</summary>
    /// <param name="schema">The schema document, as the package holds it.</param>
    /// <param name="packageSha256">The SHA-256 of the package the schema belongs to.</param>
    /// <param name="schemaPath">The path of the schema inside the package.</param>
    /// <returns>The profile's verdict and, when it was admitted, the prepared contract.</returns>
    public static ContractCompilation Compile(
        ReadOnlyMemory<byte> schema,
        string packageSha256,
        string schemaPath) =>
        CompileWithOptions(schema, packageSha256, schemaPath, DefaultPatternTimeout, checkProfile: true);

    /// <summary>The same compilation with another pattern limit; a test seam for the limit itself.</summary>
    internal static ContractCompilation CompileWithOptions(
        ReadOnlyMemory<byte> schema,
        string packageSha256,
        string schemaPath,
        TimeSpan patternTimeout) =>
        CompileWithOptions(schema, packageSha256, schemaPath, patternTimeout, checkProfile: true);

    /// <summary>The same compilation without the profile check; a test seam for the evaluator's own bounds.</summary>
    internal static ContractCompilation CompileWithoutProfileChecks(
        ReadOnlyMemory<byte> schema,
        string packageSha256,
        string schemaPath) =>
        CompileWithOptions(schema, packageSha256, schemaPath, DefaultPatternTimeout, checkProfile: false);

    /// <summary>
    /// The same compilation with the library's own defaults instead of the fixed options;
    /// a test seam for the control that shows why the format modes are fixed.
    /// </summary>
    internal static ContractCompilation CompileWithLibraryDefaults(
        ReadOnlyMemory<byte> schema,
        string packageSha256,
        string schemaPath)
    {
        var scan = SchemaScan.Read(schema);
        if (!scan.Result.IsValid)
        {
            return new ContractCompilation(scan.Result, null);
        }

        JsonSchemaEvaluator? root = null;
        JsonSchemaEvaluator? header = null;
        JsonSchemaEvaluator? record = null;
        try
        {
            var options = JsonSchemaEvaluatorOptions.Default;
            options.BaseUri = "https://report.invalid/data-contract/" + packageSha256 + "/" + schemaPath;
            root = JsonSchemaEvaluator.Compile(schema, "", options);
            header = root.ForEntryPoint(PreparedDataContract.HeaderRoot);
            record = root.ForEntryPoint(PreparedDataContract.RecordRoot);
            return new ContractCompilation(
                ProfileResult.Valid,
                new PreparedDataContract(root, header, record, scan.DeclaredNames, packageSha256, schemaPath));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            record?.Dispose();
            header?.Dispose();
            root?.Dispose();
            return new ContractCompilation(ProfileResult.Refused("compile", ""), null);
        }
    }

    private static ContractCompilation CompileWithOptions(
        ReadOnlyMemory<byte> schema,
        string packageSha256,
        string schemaPath,
        TimeSpan patternTimeout,
        bool checkProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaPath);

        var scan = SchemaScan.Read(schema);
        if (checkProfile && !scan.Result.IsValid)
        {
            return new ContractCompilation(scan.Result, null);
        }

        JsonSchemaEvaluator? root = null;
        JsonSchemaEvaluator? header = null;
        JsonSchemaEvaluator? record = null;
        try
        {
            root = JsonSchemaEvaluator.Compile(schema, "", Options(packageSha256, schemaPath, patternTimeout));
            header = root.ForEntryPoint(PreparedDataContract.HeaderRoot);
            record = root.ForEntryPoint(PreparedDataContract.RecordRoot);
            return new ContractCompilation(
                ProfileResult.Valid,
                new PreparedDataContract(root, header, record, scan.DeclaredNames, packageSha256, schemaPath));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            record?.Dispose();
            header?.Dispose();
            root?.Dispose();
            return new ContractCompilation(ProfileResult.Refused("compile", ""), null);
        }
    }

    private static JsonSchemaEvaluatorOptions Options(
        string packageSha256,
        string schemaPath,
        TimeSpan patternTimeout) => new()
        {
            DefaultDialect = JsonSchemaDialect.Draft202012,
            BaseUri = "https://report.invalid/data-contract/" + packageSha256 + "/" + schemaPath,
            DocumentResolver = NoDocument,
            FallbackDocumentResolver = null,
            FormatModes = FormatModes(),
            RegexMatchTimeout = patternTimeout,
            MaxDepth = MaximumDepth,
            CompileRegularExpressions = true,
        };

    private static Dictionary<string, JsonSchemaFormatMode> FormatModes()
    {
        var modes = new Dictionary<string, JsonSchemaFormatMode>(StringComparer.Ordinal);
        foreach (var format in DataContractProfile.Formats)
        {
            modes[format] = JsonSchemaFormatMode.Assert;
        }

        return modes;
    }

    private static bool NoDocument(string uri, out ReadOnlyMemory<byte> document)
    {
        document = default;
        return false;
    }

    private static string DescribeLibraryVersion()
    {
        var version = typeof(JsonSchemaEvaluator).Assembly.GetName().Version;
        return version is null
            ? "corvus-text-json"
            : "corvus-text-json-" + version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "." + version.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "." + version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
