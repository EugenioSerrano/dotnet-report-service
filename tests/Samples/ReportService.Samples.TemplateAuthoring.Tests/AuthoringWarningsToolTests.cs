using System.Globalization;
using System.Text;
using ReportService.Templates;

namespace ReportService.Samples.TemplateAuthoring.Tests;

/// <summary>
/// The warnings through the tool and the engines: a comma-culture package that writes a
/// formatted number into an SVG attribute renders with a warning right before its result
/// line, the same package written with the invariant function renders without one, a
/// refused render prints none, and the table of shadowing members matches what each pinned
/// engine actually answers.
/// </summary>
public sealed class AuthoringWarningsToolTests
{
    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] ProbeNames =
        ["size", "first", "last", "length", "count", "empty", "blank", "keys", "values", "index", "key", "value", "items", "type"];

    public static TheoryData<string, string, string, string> TrapsAndRemedies => new()
    {
        // The Liquid example is es-AR: an engine filter's result prints with a decimal comma.
        {
            "pilot-liquid", "html/row.html.liquid",
            "<svg width=\"10\" height=\"10\"><rect width=\"1\" height=\"{{ item.unitPrice | times: 0.01 }}\"/></svg>",
            "<svg width=\"10\" height=\"10\"><rect width=\"1\" height=\"{{ item.unitPrice | times: 0.01 | num_invariant: 2 }}\"/></svg>"
        },
        // The Handlebars example is es-AR: format_number is the reader's form.
        {
            "pilot-handlebars", "html/row.html.hbs",
            "<svg width=\"10\" height=\"10\"><rect width=\"1\" height=\"{{format_number item.unitPrice \"F2\"}}\"/></svg>",
            "<svg width=\"10\" height=\"10\"><rect width=\"1\" height=\"{{num_invariant item.unitPrice 2}}\"/></svg>"
        },
        // The Scriban example declares no locale, so the sample renders invariant: N2 groups thousands.
        {
            "pilot-simple", "html/row.html.sbn",
            "<svg width=\"10\" height=\"10\"><rect width=\"1\" height=\"{{ format_number item.unitPrice \"N2\" | html.escape }}\"/></svg>",
            "<svg width=\"10\" height=\"10\"><rect width=\"1\" height=\"{{ num_invariant item.unitPrice | html.escape }}\"/></svg>"
        },
    };

    [Theory]
    [MemberData(nameof(TrapsAndRemedies))]
    public async Task ACultureNumberInAnAttributeWarnsAndTheInvariantFunctionDoesNot(string id, string rowFile, string trap, string remedy)
    {
        var trapped = await RenderWithRowCell(id, rowFile, trap);
        var remedied = await RenderWithRowCell(id, rowFile, remedy);

        Assert.Equal(0, trapped.Code);
        var lines = trapped.Lines;
        var rendered = Array.FindIndex(lines, line => line.StartsWith("rendered ", StringComparison.Ordinal));
        Assert.Equal(lines.Length - 1, rendered);
        Assert.True(rendered > 0);
        Assert.All(lines[..rendered], line => Assert.StartsWith("warning: ", line, StringComparison.Ordinal));
        Assert.Contains("warning: culture-number-in-attribute part=row record=1 element=rect attribute=height value=", lines[0], StringComparison.Ordinal);
        Assert.Contains(",", lines[0].Split("value=")[1].Split(' ')[0], StringComparison.Ordinal);
        Assert.True(File.Exists(trapped.Html));

        Assert.Equal(0, remedied.Code);
        Assert.DoesNotContain(remedied.Lines, line => line.StartsWith("warning:", StringComparison.Ordinal));
        Assert.Matches("height=\"\\d+(\\.\\d+)?\"", File.ReadAllText(remedied.Html));
    }

    [Fact]
    public async Task AWarningChangesNeitherTheExitCodeNorTheFilesWritten()
    {
        var trap = await RenderWithRowCell("pilot-liquid", "html/row.html.liquid", "<span data-x=\"{{ item.unitPrice | times: 1 }}\" title=\"{{ item.unitPrice | times: 1 }}\"></span>");
        var withWarning = await RenderWithRowCell("pilot-liquid", "html/row.html.liquid", "<span data-x=\"{{ item.unitPrice | times: 1 }}\" width=\"{{ item.unitPrice | times: 1 }}\"></span>");

        Assert.Equal(0, trap.Code);
        Assert.DoesNotContain(trap.Lines, line => line.StartsWith("warning:", StringComparison.Ordinal));
        Assert.Equal(0, withWarning.Code);
        Assert.Contains(withWarning.Lines, line => line.StartsWith("warning:", StringComparison.Ordinal));
        Assert.True(File.Exists(withWarning.Pdf));
    }

    [Fact]
    public async Task AReservedMemberWarningNamesTheFileTheLineAndTheMember()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage("pilot-liquid", root);
        var rowPath = Path.Combine(package, "html", "row.html.liquid");
        var row = File.ReadAllText(rowPath);
        File.WriteAllText(rowPath, row.Replace("</tr>", "<td data-n=\"{{ header.customer.size }}\"></td></tr>", StringComparison.Ordinal), Utf8NoBom);

        // The header gains a field named size: the attribute holds the member count instead.
        var samplePath = Path.Combine(package, "sample.json");
        var sample = File.ReadAllText(samplePath);
        File.WriteAllText(samplePath, sample.Replace("\"customer\": {", "\"customer\": {\n      \"size\": \"large\",", StringComparison.Ordinal), Utf8NoBom);

        var schemaPath = Path.Combine(package, "schema.json");
        var schema = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(schemaPath))!;
        schema["properties"]!["header"]!["properties"]!["customer"]!["properties"]!["size"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" };
        File.WriteAllText(schemaPath, schema.ToJsonString(), Utf8NoBom);

        var (code, lines, _, _) = await Render(package, root);

        Assert.True(code == 0, string.Join(" | ", lines));
        var warning = Assert.Single(lines, line => line.StartsWith("warning:", StringComparison.Ordinal));
        Assert.StartsWith("warning: reserved-member-shadows-field file=html/row.html.liquid line=1 column=", warning, StringComparison.Ordinal);
        Assert.Contains("expression=header.customer.size field=header.customer.size", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedRenderPrintsNoWarning()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage("pilot-liquid", root);
        var rowPath = Path.Combine(package, "html", "row.html.liquid");
        var row = File.ReadAllText(rowPath);
        File.WriteAllText(
            rowPath,
            row.Replace("{{ item.description }}", "{{ item.description | raw }}<rect height=\"{{ item.unitPrice | times: 1 }}\"/>", StringComparison.Ordinal),
            Utf8NoBom);

        var (code, lines, _, _) = await Render(package, root);

        Assert.Equal(1, code);
        Assert.DoesNotContain(lines, line => line.StartsWith("warning:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("scriban")]
    [InlineData("handlebars")]
    [InlineData("liquid")]
    public async Task TheShadowingMembersAreExactlyWhatThePinnedEngineAnswers(string engine)
    {
        var fields = ProbeNames.ToDictionary(name => name, name => (object?)("field-" + name), StringComparer.Ordinal);
        var source = string.Concat(ProbeNames.Select(name => engine switch
        {
            "scriban" => "[{{ item." + name + " | html.escape }}]",
            "handlebars" => "[{{item." + name + "}}]",
            _ => "[{{ item." + name + " }}]",
        }));

        var printed = await ComposeRow(engine, source, fields);

        var answered = printed.Trim('[', ']').Split("][");
        var shadowing = ProbeNames.Where((name, index) => answered[index] != "field-" + name).ToArray();
        Assert.Equal(ReservedMembers.Of(engine), shadowing);
    }

    private static async Task<string> ComposeRow(string engine, string source, Dictionary<string, object?> fields)
    {
        var adapter = TemplateEngineAdapters.All().Single(candidate => candidate.Engine == engine);
        var path = "html/row.html." + (engine == "handlebars" ? "hbs" : engine == "liquid" ? "liquid" : "sbn");
        var manifest = new RenderManifest(
            engine,
            adapter.Version,
            new Dictionary<TemplatePart, string> { [TemplatePart.Row] = path },
            [],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults("en-US", "UTC"),
            GroupKey: null);
        var prepared = adapter.Prepare(manifest, new SingleFile(path, source));
        var settings = new PresentationSettings(CultureInfo.GetCultureInfo("en-US"), TimeZoneInfo.Utc, FixedInstant);
        var block = await new BlockComposer(new FixedTimeProvider(FixedInstant)).ComposeAsync(
            new CompositionRequest(
                prepared,
                new CompositionBlock(0, 1, 1, IsFirst: true, IsLast: true),
                new Dictionary<string, object?>(StringComparer.Ordinal),
                [new ComposedRecord(1, fields)],
                new CompositionRender("en-US", "UTC", FixedInstant),
                null),
            adapter,
            settings,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        return block.TextOf(block.Rows.Single());
    }

    private static async Task<(int Code, string[] Lines, string Html, string Pdf)> RenderWithRowCell(string id, string rowFile, string cell)
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(id, root);
        var rowPath = Path.Combine(package, rowFile.Replace('/', Path.DirectorySeparatorChar));
        var row = File.ReadAllText(rowPath);
        var changed = row.Replace("</tr>", "<td>" + cell + "</td></tr>", StringComparison.Ordinal);
        Assert.NotEqual(row, changed);
        File.WriteAllText(rowPath, changed, Utf8NoBom);
        return await Render(package, root, id);
    }

    private static async Task<(int Code, string[] Lines, string Html, string Pdf)> Render(string package, string root, string? id = null)
    {
        id ??= Path.GetFileName(package);
        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, id + ".pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await new TemplateAuthoringTool(new FixedTimeProvider(FixedInstant), () => printer)
            .RenderAsync(new RenderRequest(package, outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        return (code, lines, Path.Combine(outputDirectory, id + "-1.0.0.html"), Path.Combine(outputDirectory, id + "-1.0.0.pdf"));
    }

    private static string CopyPackage(string id, string root)
    {
        var source = TestPaths.Example(id);
        var target = Path.Combine(root, id);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    private sealed class SingleFile(string path, string text) : ITemplatePackageFiles
    {
        public bool Exists(string name) => name == path;

        public Stream OpenRead(string name) => new MemoryStream(Encoding.UTF8.GetBytes(ReadAllText(name)));

        public string ReadAllText(string name) => name == path ? text : throw new TemplatePackageException("absent");
    }
}
