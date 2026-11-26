using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ReportService.Testing.Fixtures;

namespace ReportService.Testing.TemplatePackages;

/// <summary>
/// The page-accounting fixture package: a report whose template declares the printed page
/// totals with their accounting contract, so a print runs on the accounting path. It is a
/// Scriban package with a grid row (one root element, a literal amount role), a group part
/// whose total cell carries the group-total role, the reserved band inside the bottom
/// margin with the template's three Spanish labels, the declared grouping by <c>groupId</c>,
/// a packaged Liberation Sans font and the global-numbering contract whose reserved area
/// sits in the top margin, disjoint from the band. The declaration of the geometry, the
/// labels, the scale and the culture is also committed as
/// <c>OracleExpectations/page-accounting.json</c>, and a hermetic case holds the two to
/// each other so neither drifts.
/// </summary>
public static class PageAccountingFixtures
{
    /// <summary>The package's template identifier.</summary>
    public const string TemplateId = "page-accounting";

    /// <summary>The version the fixture declares.</summary>
    public const string Version = "1.0.0";

    /// <summary>The record field the totals sum.</summary>
    public const string AmountField = "amount";

    /// <summary>The record field the records are grouped by.</summary>
    public const string GroupKey = "groupId";

    /// <summary>The fraction digits the printed accounting values carry.</summary>
    public const int Scale = 2;

    /// <summary>The culture the accounting band and the numbering are written in.</summary>
    public const string Culture = "es-AR";

    /// <summary>The time zone the presentation resolves under.</summary>
    public const string TimeZone = "America/Argentina/Buenos_Aires";

    /// <summary>The band's label of the brought-forward line.</summary>
    public const string BroughtForwardLabel = "Saldo anterior";

    /// <summary>The band's label of the page-subtotal line.</summary>
    public const string PageSubtotalLabel = "Subtotal de pagina";

    /// <summary>The band's label of the carried-forward line.</summary>
    public const string CarriedForwardLabel = "Saldo siguiente";

    /// <summary>The band's distance from the page's left edge, in millimetres.</summary>
    public const decimal BandLeftMm = 12m;

    /// <summary>The band's distance from the page's top edge, in millimetres.</summary>
    public const decimal BandTopMm = 280m;

    /// <summary>The band's width, in millimetres.</summary>
    public const decimal BandWidthMm = 186m;

    /// <summary>The band's height, in millimetres.</summary>
    public const decimal BandHeightMm = 14m;

    /// <summary>The page's height, in millimetres.</summary>
    public const decimal PageHeightMm = 297m;

    /// <summary>The page's width, in millimetres.</summary>
    public const decimal PageWidthMm = 210m;

    /// <summary>The numbering area's distance from the page's left edge, in millimetres.</summary>
    public const decimal NumberingLeftMm = 12m;

    /// <summary>The numbering area's distance from the page's top edge, in millimetres, inside the top margin.</summary>
    public const decimal NumberingTopMm = 4m;

    /// <summary>The numbering area's width, in millimetres.</summary>
    public const decimal NumberingWidthMm = 70m;

    /// <summary>The numbering area's height, in millimetres.</summary>
    public const decimal NumberingHeightMm = 8m;

    /// <summary>The numbering contract's wording, with exactly one <c>{X}</c> and one <c>{Y}</c>.</summary>
    public const string NumberingFormat = "Pagina {X} de {Y}";

    /// <summary>The packaged font's package-relative path.</summary>
    public const string FontPath = "html/fonts/LiberationSans-Regular.ttf";

    /// <summary>The package's sample: three records in two groups, amounts exact to the cent.</summary>
    public const string Sample = """
        {
          "contractVersion": "1.0",
          "header": { "reportTitle": "Page accounting sample", "currency": "ARS" },
          "items": [
            { "sequence": 1, "groupId": "G1", "groupName": "North", "date": "2026-09-01", "description": "First", "amount": 10.00 },
            { "sequence": 2, "groupId": "G1", "groupName": "North", "date": "2026-09-02", "description": "Second", "amount": 2.50 },
            { "sequence": 3, "groupId": "G2", "groupName": "South", "date": "2026-09-03", "description": "Third", "amount": 4.25 }
          ]
        }
        """;

    /// <summary>The package's files, as a package a template source publishes.</summary>
    /// <param name="owner">The consumer that owns the version.</param>
    /// <param name="templateId">The template identifier, or the fixture's own.</param>
    /// <param name="amountField">The amount field, or the fixture's own; a non-numeric field is the known-bad control.</param>
    public static Dictionary<string, byte[]> Files(
        string owner,
        string templateId = TemplateId,
        string amountField = AmountField)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentException.ThrowIfNullOrEmpty(amountField);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["template.yaml"] = utf8.GetBytes(Manifest(owner, templateId, amountField)),
            ["schema.json"] = utf8.GetBytes(Schema),
            ["sample.json"] = utf8.GetBytes(Sample),
            ["html/document-start.html.sbn"] = utf8.GetBytes(DocumentStart),
            ["html/group-start.html.sbn"] = utf8.GetBytes(GroupStart),
            ["html/row.html.sbn"] = utf8.GetBytes(Row),
            ["html/group-end.html.sbn"] = utf8.GetBytes(GroupEnd),
            ["html/document-end.html.sbn"] = utf8.GetBytes(DocumentEnd),
            ["html/styles.css"] = utf8.GetBytes(Stylesheet),
            [FontPath] = Font(),
        };
    }

    /// <summary>The package's files written into <paramref name="directory"/>, the form the rendering host mounts.</summary>
    /// <param name="directory">The package folder to create.</param>
    /// <param name="owner">The consumer that owns the version.</param>
    /// <param name="templateId">The template identifier, or the fixture's own.</param>
    public static void WriteFolder(string directory, string owner, string templateId = TemplateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        System.IO.Directory.CreateDirectory(directory);
        foreach (var (path, bytes) in Files(owner, templateId))
        {
            var file = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
        }
    }

    /// <summary>The packaged font's bytes, from the test support's own copy.</summary>
    public static byte[] Font() =>
        File.ReadAllBytes(Path.Combine(
            RepositoryPaths.Root,
            "tests",
            "Support",
            "ReportService.Testing",
            "Fonts",
            "LiberationSans-Regular.ttf"));

    /// <summary>The package's manifest.</summary>
    /// <param name="owner">The consumer that owns the version.</param>
    /// <param name="templateId">The template identifier.</param>
    /// <param name="amountField">The record field the totals sum.</param>
    public static string Manifest(string owner, string templateId, string amountField) => $"""
        manifestVersion: "1.0"
        id: {templateId}
        version: "{Version}"
        owner: {owner}
        shared: false
        engine: scriban
        engineVersion: "7.5.1"
        dataContract:
          version: "1.0"
          profile: header-items-v1
          schema: schema.json
        outputs:
          html:
            parts:
              documentStart: html/document-start.html.sbn
              groupStart: html/group-start.html.sbn
              row: html/row.html.sbn
              groupEnd: html/group-end.html.sbn
              documentEnd: html/document-end.html.sbn
            styles: [html/styles.css]
          pdf:
            paper: A4
            orientation: portrait
            marginMm:
              top: 15
              right: 12
              bottom: 18
              left: 12
            pageTotals:
              enabled: true
              amountField: {amountField}
              scale: {Scale.ToString(CultureInfo.InvariantCulture)}
              footer:
                leftMm: {Millimetres(BandLeftMm)}
                topMm: {Millimetres(BandTopMm)}
                widthMm: {Millimetres(BandWidthMm)}
                heightMm: {Millimetres(BandHeightMm)}
                labels:
                  broughtForward: "{BroughtForwardLabel}"
                  pageSubtotal: "{PageSubtotalLabel}"
                  carriedForward: "{CarriedForwardLabel}"
            numbering:
              area:
                leftMm: {Millimetres(NumberingLeftMm)}
                topMm: {Millimetres(NumberingTopMm)}
                widthMm: {Millimetres(NumberingWidthMm)}
                heightMm: {Millimetres(NumberingHeightMm)}
              font:
                family: "Liberation Sans"
                file: {FontPath}
                sizePt: 9
              format: "{NumberingFormat}"
              culture: {Culture}
            verification:
              anchors:
                header: ["$.reportTitle"]
                item: ["$.description"]
        partitioning:
          groupKey: {GroupKey}
        localeDefaults:
          culture: {Culture}
          timeZone: {TimeZone}
        title: "Page accounting"
        """;

    /// <summary>The schema of the account-taking snapshot.</summary>
    public const string Schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "contractVersion": { "type": "string" },
            "header": {
              "type": "object",
              "properties": {
                "reportTitle": { "type": "string" },
                "currency": { "type": "string" }
              },
              "required": ["reportTitle"]
            },
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "sequence": { "type": "integer" },
                  "groupId": { "type": "string" },
                  "groupName": { "type": "string" },
                  "date": { "type": "string" },
                  "description": { "type": "string" },
                  "amount": { "type": "number" }
                },
                "required": ["sequence", "groupId", "description", "amount"]
              }
            }
          },
          "required": ["contractVersion", "header", "items"]
        }
        """;

    /// <summary>The document start: the header and the column captions the judge reads once.</summary>
    public const string DocumentStart = """
        <html><head><link rel="stylesheet" href="html/styles.css"></head><body>
        <header class="doc">
          <h1>{{ header.reportTitle | html.escape }}</h1>
          <div class="head-row"><span>Seq</span><span>Group</span><span>Description</span><span>Amount</span></div>
        </header>
        """;

    /// <summary>The group start: the group's key, printed above its own rows.</summary>
    public const string GroupStart = """<div class="group"><div class="group-key">{{ group.key | html.escape }}</div>""";

    /// <summary>The row: one grid item, the amount cell carrying the literal amount role.</summary>
    public const string Row = """
        <div class="item"><div class="seq">{{ block.recordOrdinal }}</div><div class="grp">{{ group.key | html.escape }}</div><div class="desc">{{ item.description | html.escape }}</div><div class="amount" data-role="amount">{{ item.amount | html.escape }}</div></div>
        """;

    /// <summary>The group end: the group's key and the cell the layout writes its total into.</summary>
    public const string GroupEnd = """<div class="group-total"><span class="group-key">{{ group.key | html.escape }}</span><span class="group-sum" data-role="group-total"></span></div></div>""";

    /// <summary>The document end.</summary>
    public const string DocumentEnd = """</body></html>""";

    /// <summary>The stylesheet: the packaged family, the four-column grid and the page box.</summary>
    public const string Stylesheet = """
        @font-face {
          font-family: "Liberation Sans";
          src: url("fonts/LiberationSans-Regular.ttf");
        }

        @page {
          size: A4 portrait;
          margin: 15mm 12mm 18mm 12mm;
        }

        body {
          margin: 0;
          font-family: "Liberation Sans", sans-serif;
          font-size: 9pt;
        }

        .head-row, .item {
          display: grid;
          grid-template-columns: 12mm 20mm 1fr 30mm;
        }

        .head-row > span,
        .item > div,
        .group-key,
        .group-sum {
          font-family: "Liberation Sans", sans-serif;
        }

        .head-row > span {
          font-weight: bold;
        }

        .desc {
          overflow-wrap: anywhere;
        }

        .group-key {
          font-weight: bold;
        }

        .group-total {
          text-align: right;
        }
        """;

    /// <summary>
    /// The first <paramref name="records"/> records of the pilot grouped dataset, with a
    /// header the fixture package prints and the amount and group key of every record kept
    /// for the independent oracle. The dataset's per-group structure gives one group larger
    /// than a block under the governed limits, so a group spans a block boundary.
    /// </summary>
    /// <param name="records">How many records the snapshot holds.</param>
    /// <param name="reportTitle">The header title the package prints.</param>
    public static (byte[] Snapshot, IReadOnlyList<AccountedRecord> Records) ReadGroupedSlice(
        int records,
        string reportTitle = "Page accounting statement")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(records);
        var path = Path.Combine(RepositoryPaths.Root, "samples", "datasets", "header-items-v1.grouped.10k.json");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The pilot dataset {path} is missing. Generate it with: dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate");
        }

        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var source = document["items"]!.AsArray();
        if (source.Count < records)
        {
            throw new InvalidOperationException("the pilot grouped dataset holds fewer records than the fixture needs");
        }

        var header = new JsonObject
        {
            ["reportTitle"] = reportTitle,
            ["currency"] = "ARS",
            ["totals"] = new JsonObject { ["recordCount"] = records },
        };
        var items = new JsonArray();
        var kept = new List<AccountedRecord>(records);
        for (var index = 0; index < records; index++)
        {
            var item = (JsonObject)source[index]!.DeepClone();
            items.Add(item);
            kept.Add(new AccountedRecord(
                item["sequence"]!.GetValue<long>(),
                item[GroupKey]!.GetValue<string>(),
                item[AmountField]!.GetValue<decimal>()));
        }

        var snapshot = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["header"] = header,
            ["items"] = items,
        };
        return (Encoding.UTF8.GetBytes(snapshot.ToJsonString()), kept);
    }

    /// <summary>The invariant text of a millimetre value, as the manifest writes it.</summary>
    /// <param name="value">The value.</param>
    private static string Millimetres(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
