using ReportService.TemplatePackages.Manifest;

namespace ReportService.TemplatePackages.Tests.Manifest;

/// <summary>The per-page code declaration is a PDF layout field of the manifest: read whole, absent by default.</summary>
public sealed class PageCodeManifestTests
{
    [Fact]
    public void ADeclaredPageCodeIsReadWithEveryField()
    {
        const string json = """
            {
              "manifestVersion": "1.0",
              "id": "invoices",
              "version": "1.0.0",
              "outputs": {
                "pdf": {
                  "paper": "A4",
                  "pageCode": {
                    "generator": "acme-qr",
                    "options": { "errorCorrection": "M" },
                    "area": { "leftMm": 170, "topMm": 10, "widthMm": 25, "heightMm": 25 },
                    "source": {
                      "value": "{page.number}|{match}",
                      "match": { "pattern": "INV-[0-9]{8}", "maxLength": 12 }
                    }
                  }
                }
              }
            }
            """;

        var code = TemplateManifestJson.ReadTyped(json).Outputs!.Pdf!.PageCode!;

        Assert.Equal("acme-qr", code.Generator);
        Assert.Equal("M", code.Options!["errorCorrection"]);
        Assert.Equal(170m, code.Area!.LeftMm);
        Assert.Equal(25m, code.Area.HeightMm);
        Assert.Equal("{page.number}|{match}", code.Source!.Value);
        Assert.Equal("INV-[0-9]{8}", code.Source.Match!.Pattern);
        Assert.Equal(12, code.Source.Match.MaxLength);
    }

    [Fact]
    public void AManifestWithoutAPageCodeHasNone()
    {
        const string json = """{ "manifestVersion": "1.0", "id": "invoices", "version": "1.0.0", "outputs": { "pdf": { "paper": "A4" } } }""";

        Assert.Null(TemplateManifestJson.ReadTyped(json).Outputs!.Pdf!.PageCode);
    }
}
