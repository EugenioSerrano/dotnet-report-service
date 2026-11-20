using System.Text.Json;

namespace ReportService.Fixtures.Tests;

/// <summary>
/// The rendering host's <c>sample</c> verb: it takes no fixture, reads the package's own
/// <c>template.yaml</c> and <c>sample.json</c>, and answers a usage error, an unreadable
/// package and a deterministic refusal - the three paths that never reach a browser.
/// The printed sample is the rendering level's case; this suite holds the verb's own
/// contract, hermetic.
/// </summary>
public sealed class SampleVerbTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sample-verb-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TheSampleVerbWithoutItsOptionsIsAUsageError()
    {
        Assert.Equal(2, await ReportService.RenderingHost.Program.Main(["sample"]));
        Assert.Equal(2, await ReportService.RenderingHost.Program.Main(["sample", "--fixture", "x", "--template", _root, "--out", _root]));
    }

    [Fact]
    public async Task APackageThatCannotBeReadIsAFailureNamingTheFile()
    {
        Assert.Equal(1, await ReportService.RenderingHost.Program.Main(["sample", "--template", Path.Combine(_root, "absent"), "--out", Path.Combine(_root, "out")]));
    }

    [Fact]
    public async Task APackageWhoseSampleCannotBeVerifiedIsRefusedAndWritesItsResult()
    {
        var package = WritePackage();
        var outFolder = Path.Combine(_root, "out");

        var exitCode = await ReportService.RenderingHost.Program.Main(["sample", "--template", package, "--out", outFolder]);

        Assert.Equal(0, exitCode);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outFolder, "no-pdf.json"), TestContext.Current.CancellationToken));
        Assert.Equal("refused", result.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Verification", result.RootElement.GetProperty("failureKind").GetString());
        Assert.Equal("template.yaml", result.RootElement.GetProperty("failurePart").GetString());
        Assert.Equal("anchors-undeclared", result.RootElement.GetProperty("failureReason").GetString());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("pdf").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("htmlSha256").ValueKind);
    }

    private string WritePackage()
    {
        var package = Path.Combine(_root, "no-pdf");
        Directory.CreateDirectory(Path.Combine(package, "html"));
        File.WriteAllText(
            Path.Combine(package, "template.yaml"),
            """
            manifestVersion: "1.0"
            id: no-pdf
            version: "1.0.0"
            owner: demo
            shared: true
            engine: scriban
            engineVersion: "7.5.1"
            dataContract: { version: "1.0", profile: header-items-v1, schema: schema.json }
            outputs:
              html:
                parts:
                  documentStart: html/document-start.html.sbn
                  row: html/row.html.sbn
                  documentEnd: html/document-end.html.sbn
            """);
        File.WriteAllText(Path.Combine(package, "schema.json"), "{}");
        File.WriteAllText(Path.Combine(package, "sample.json"), """{"contractVersion":"1.0","header":{"title":"x"},"items":[]}""");
        File.WriteAllText(Path.Combine(package, "html", "document-start.html.sbn"), "<html><body>");
        File.WriteAllText(Path.Combine(package, "html", "row.html.sbn"), "<tr></tr>");
        File.WriteAllText(Path.Combine(package, "html", "document-end.html.sbn"), "</body></html>");
        return package;
    }
}
