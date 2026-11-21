using System.Text.Json.Nodes;
using ReportService.TemplateChecks.Checks;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Tests;

/// <summary>A package written to a temporary directory for one test.</summary>
internal sealed class TempPackage : IDisposable
{
    internal TempPackage(string directory) => Directory = directory;

    /// <summary>The package's directory.</summary>
    public string Directory { get; }

    /// <summary>Writes the files, by package-relative path.</summary>
    public static TempPackage Write(params (string Path, string Text)[] files)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("template-checks-").FullName;
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            _ = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        return new TempPackage(directory);
    }

    /// <summary>A candidate over this package.</summary>
    public TemplateCandidate Candidate(string manifestJson, string version = "1.1.0") =>
        new(
            "invoice-energy",
            version,
            TemplateManifestJson.ReadTyped(manifestJson),
            manifestJson,
            Directory,
            "sha256:" + new string('a', 64));

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The manifests, schemas, samples and parts the tests use.</summary>
internal static class Packages
{
    internal const string Schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "contractVersion": { "type": "string" },
            "header": { "type": "object", "properties": { "title": { "type": "string" } }, "required": ["title"] },
            "items": { "type": "array", "items": { "type": "object", "properties": { "quantity": { "type": "integer" } }, "required": ["quantity"] } }
          },
          "required": ["contractVersion", "header", "items"]
        }
        """;

    internal const string Sample = """{ "contractVersion": "1.0", "header": { "title": "Invoice" }, "items": [ { "quantity": 1 } ] }""";

    internal static string Extension(string engine) => engine switch
    {
        "handlebars" => ".hbs",
        "liquid" => ".liquid",
        _ => ".sbn",
    };

    internal static string Row(string engine) => engine switch
    {
        "handlebars" => "<tr><td>{{ item.quantity }}</td></tr>",
        "liquid" => "<tr><td>{{ item.quantity }}</td></tr>",
        _ => "<tr><td>{{ item.quantity | html.escape }}</td></tr>",
    };

    internal static string ManifestJson(
        string engine = "scriban",
        string? schema = "schema.json",
        string? row = null,
        string documentStart = "",
        string documentEnd = "",
        string styles = "html/styles.css",
        string? engineVersion = "7.5.1",
        bool withPdf = true,
        string paper = "A4",
        string orientation = "portrait",
        string? margin = null,
        string? aggregates = null,
        string? verifiedTotals = null,
        string? groupOrder = null)
    {
        var extension = Extension(engine);
        var parts = new JsonObject
        {
            ["documentStart"] = documentStart.Length > 0 ? documentStart : "html/document-start.html" + extension,
            ["row"] = row ?? "html/row.html" + extension,
            ["documentEnd"] = documentEnd.Length > 0 ? documentEnd : "html/document-end.html" + extension,
        };

        var dataContract = new JsonObject
        {
            ["version"] = "1.0",
            ["profile"] = "header-items-v1",
        };
        if (schema is not null)
        {
            dataContract["schema"] = schema;
        }

        if (verifiedTotals is not null)
        {
            dataContract["verifiedTotals"] = verifiedTotals == "true";
        }

        var outputs = new JsonObject
        {
            ["html"] = new JsonObject
            {
                ["parts"] = parts.DeepClone(),
                ["styles"] = new JsonArray(styles),
            },
        };

        if (withPdf)
        {
            var margins = margin ?? """
                { "top": 15, "right": 12, "bottom": 18, "left": 12 }
                """;
            outputs["pdf"] = new JsonObject
            {
                ["paper"] = paper,
                ["orientation"] = orientation,
                ["marginMm"] = JsonNode.Parse(margins),
                ["verification"] = new JsonObject
                {
                    ["anchors"] = new JsonObject
                    {
                        ["header"] = new JsonArray("$.title"),
                        ["item"] = new JsonArray("$.quantity"),
                    },
                },
            };
        }

        var manifest = new JsonObject
        {
            ["manifestVersion"] = "1.0",
            ["id"] = "invoice-energy",
            ["version"] = "1.1.0",
            ["owner"] = "billing-system",
            ["shared"] = false,
            ["engine"] = engine,
            ["dataContract"] = dataContract,
            ["outputs"] = outputs,
        };

        if (engineVersion is not null)
        {
            manifest["engineVersion"] = engineVersion;
        }

        if (aggregates is not null)
        {
            manifest["aggregates"] = new JsonArray(new JsonObject { ["name"] = "total" });
        }

        if (groupOrder is not null)
        {
            manifest["partitioning"] = new JsonObject { ["groupKey"] = "$.group", ["groupOrder"] = groupOrder };
        }

        return manifest.ToJsonString();
    }

    internal static TempPackage Package(string engine = "scriban", string? row = null)
    {
        var extension = Extension(engine);
        return TempPackage.Write(
            ("schema.json", Schema),
            ("sample.json", Sample),
            ("html/document-start.html" + extension, "<html><body>"),
            ("html/row.html" + extension, row ?? Row(engine)),
            ("html/document-end.html" + extension, "</body></html>"),
            ("html/styles.css", "body { font-family: \"Liberation Sans\", sans-serif; }"));
    }

    internal static IReadOnlyList<ITemplateVersionCheck> Checks(TimeProvider? clock = null)
    {
        var session = new TemplateCheckSession(clock ?? TimeProvider.System);
        return
        [
            new ManifestAndPathsCheck(session),
            new SchemaCheck(session),
            new SampleCheck(session, clock ?? TimeProvider.System),
            new AnchorCheck(session),
            new SyntaxCheck(session),
            new IncludeAndAssetCheck(session),
            new TemplateLintCheck(session),
            new EngineVersionCheck(),
            new PageAccountingCheck(),
            new NamedFontFamilyCheck(session),
            new BottomAlignedGridCellCheck(session),
            new ReportService.TemplateChecks.Sample.SampleRenderCheck(new ReportService.Testing.TemplateChecks.InMemorySampleRenderer().AlwaysRendered()),
        ];
    }

    internal static async Task<TemplateCheckResult> RunAsync(
        IReadOnlyList<ITemplateVersionCheck> checks,
        TemplateCandidate candidate)
    {
        foreach (var check in checks)
        {
            var result = await check.CheckAsync(candidate, TestContext.Current.CancellationToken);
            if (!result.Passed)
            {
                return result;
            }
        }

        return TemplateCheckResult.Pass();
    }

    /// <summary>A package written from the bytes of a fixture, by package-relative path.</summary>
    /// <param name="files">The files, by package-relative path.</param>
    internal static TempPackage WritePackage(IReadOnlyDictionary<string, byte[]> files)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("template-checks-").FullName;
        foreach (var (path, bytes) in files)
        {
            var full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            _ = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        return new TempPackage(directory);
    }

    /// <summary>
    /// A candidate over a package the tree holds, its manifest read through the
    /// product's own reader: the tests that read a package nobody wrote for them
    /// judge the files as the take reads them.
    /// </summary>
    /// <param name="directory">The package's directory.</param>
    internal static TemplateCandidate ReadCandidate(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "template.yaml"));
        var read = new ReportService.TemplateSource.Manifest.TemplateManifestReader().Read(stream);
        Assert.True(read.Succeeded, read.Reason);
        var manifest = Assert.IsType<TemplateManifest>(read.Manifest);
        return new TemplateCandidate(
            manifest.Id ?? string.Empty,
            manifest.Version ?? string.Empty,
            manifest,
            read.CanonicalJson!,
            directory,
            "sha256:" + new string('a', 64));
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _timestamp;

    /// <inheritdoc />
    public override long GetTimestamp() => _timestamp;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => _timestamp += (long)(by.TotalSeconds * TimestampFrequency);

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}
