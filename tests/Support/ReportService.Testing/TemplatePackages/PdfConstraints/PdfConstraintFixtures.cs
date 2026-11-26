using System.Text;

namespace ReportService.Testing.TemplatePackages;

/// <summary>
/// The PDF constraint fixtures: one report written for each engine, whose PDF body
/// uses table markup, whose stylesheet names a real font family on a rule that
/// matches its cells directly, and whose manifest declares the printed page totals
/// off. The take's PDF constraints admit them, which makes them the base every
/// mutation of these cases starts from: a version that turns the totals on (the
/// page-subtotal case) or leaves the declaration out (one of its negative twins),
/// one whose family is only a generic keyword, and the data-shape package whose
/// grouped row names no family while its plain row does.
/// </summary>
public static class PdfConstraintFixtures
{
    /// <summary>The template's identifier, shared by every package here.</summary>
    public const string TemplateId = "invoice-pdf";

    /// <summary>The version every package here declares.</summary>
    public const string Version = "1.0.0";

    /// <summary>The consumer the manifests name.</summary>
    public const string Owner = "billing-system";

    /// <summary>The version each engine's delivered rules accept.</summary>
    /// <param name="engine">The engine the package declares.</param>
    public static string EngineVersion(string engine) => engine switch
    {
        "handlebars" => "2.4.3",
        "liquid" => "2.40.0",
        _ => "7.5.1",
    };

    /// <summary>The part extension of an engine.</summary>
    /// <param name="engine">The engine the package declares.</param>
    public static string Extension(string engine) => engine switch
    {
        "handlebars" => ".hbs",
        "liquid" => ".liquid",
        _ => ".sbn",
    };

    /// <summary>
    /// A control package: table-markup detail, the family named on the cell's own
    /// class, the page totals declared off.
    /// </summary>
    /// <param name="engine">The engine the package declares.</param>
    public static Dictionary<string, byte[]> Control(string engine)
    {
        var extension = Extension(engine);
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["template.yaml"] = Encoding.UTF8.GetBytes(Manifest(engine, "enabled: false")),
            ["schema.json"] = Encoding.UTF8.GetBytes(Schema),
            ["sample.json"] = Encoding.UTF8.GetBytes(Sample),
            ["html/document-start.html" + extension] = Encoding.UTF8.GetBytes("<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body>"),
            ["html/row.html" + extension] = Encoding.UTF8.GetBytes(Row(engine)),
            ["html/document-end.html" + extension] = Encoding.UTF8.GetBytes("</body></html>"),
            ["html/styles.css"] = Encoding.UTF8.GetBytes(Stylesheet),
        };
    }

    /// <summary>The control with the printed page totals declared on.</summary>
    /// <param name="engine">The engine the package declares.</param>
    public static Dictionary<string, byte[]> PageTotalsEnabled(string engine) =>
        Control(engine).With("template.yaml", Manifest(engine, "enabled: true"));

    /// <summary>The control with the printed page totals declared off.</summary>
    /// <param name="engine">The engine the package declares.</param>
    public static Dictionary<string, byte[]> PageTotalsDeclaredOff(string engine) =>
        Control(engine).With("template.yaml", Manifest(engine, "enabled: false"));

    /// <summary>
    /// The control with the family on the cells' own rule replaced by a generic
    /// keyword: the detail text of the version names no family, and the part the
    /// message names is the engine's own row part.
    /// </summary>
    /// <param name="engine">The engine the package declares.</param>
    public static Dictionary<string, byte[]> FontFamilyUnnamed(string engine) =>
        Control(engine).With(
            "html/styles.css",
            Stylesheet.Replace("\"Liberation Sans\", sans-serif", "sans-serif", StringComparison.Ordinal));

    /// <summary>
    /// The data-shape package: one row part writing the plain row and a grouped row
    /// the template prints only when a group exists. The plain cell's class rule
    /// names a family; the grouped cell carries a class of its own whose rule sets
    /// only a generic keyword and comes later in the stylesheet, so the grouped cell
    /// is painted in the generic family although the package's sample prints fine.
    /// </summary>
    public static Dictionary<string, byte[]> GroupedVariants()
    {
        var package = Control("scriban");
        package["html/row.html.sbn"] = Encoding.UTF8.GetBytes(GroupedVariantsRow);
        package["html/styles.css"] = Encoding.UTF8.GetBytes(GroupedVariantsStylesheet);
        return package;
    }

    /// <summary>The row part of <see cref="GroupedVariants"/>: the plain row and the grouped one.</summary>
    public const string GroupedVariantsRow = """
        <tr class="plain"><td class="cell">{{ item.quantity | html.escape }}</td></tr>
        {{ if group }}<tr class="grouped"><td class="cell cell-group">{{ group.name | html.escape }}</td></tr>{{ end }}
        """;

    /// <summary>
    /// The stylesheet of <see cref="GroupedVariants"/>: the plain cell's rule names a
    /// family, the grouped cell's own rule sets a generic keyword and is the later of
    /// the two at the same specificity.
    /// </summary>
    public const string GroupedVariantsStylesheet = """
        body {
          margin: 0;
        }

        .cell {
          font-family: "Liberation Sans", sans-serif;
        }

        .cell-group {
          font-family: sans-serif;
        }
        """;

    /// <summary>The control with the printed page totals left out of the manifest.</summary>
    /// <param name="engine">The engine the package declares.</param>
    public static Dictionary<string, byte[]> PageTotalsAbsent(string engine) =>
        Control(engine).With("template.yaml", Manifest(engine, "enabled: null"));

    /// <summary>
    /// The control with the row part rewritten to a grid container whose one cell is
    /// bottom-aligned and declares no break avoidance: the defect the grid rule
    /// refuses. Two engines align the cell through its own rule, the third through the
    /// container's, so both declarations that rule names are exercised.
    /// </summary>
    /// <param name="engine">The engine the package declares.</param>
    public static Dictionary<string, byte[]> GridBottom(string engine)
    {
        var package = Control(engine);
        package["html/row.html" + Extension(engine)] = Encoding.UTF8.GetBytes(GridRow(engine));
        package["html/styles.css"] = Encoding.UTF8.GetBytes(GridStylesheet(engine));
        return package;
    }

    /// <summary>The row part of <see cref="GridBottom"/>: a grid container and one cell.</summary>
    /// <param name="engine">The engine the package declares.</param>
    public static string GridRow(string engine) =>
        "<div class=\"grid\"><div class=\"amount\">" + Record(engine) + "</div></div>";

    /// <summary>
    /// The stylesheet of <see cref="GridBottom"/>: the container is a grid, and the cell
    /// is bottom-aligned by its own rule for two engines and by the container's for the
    /// third. The cell declares no break avoidance, so it may split across pages.
    /// </summary>
    /// <param name="engine">The engine the package declares.</param>
    public static string GridStylesheet(string engine) => engine == "liquid"
        ? """
          body {
            margin: 0;
            font-family: "Liberation Sans", sans-serif;
          }

          .grid {
            display: grid;
            align-items: end;
          }
          """
        : """
          body {
            margin: 0;
            font-family: "Liberation Sans", sans-serif;
          }

          .grid {
            display: grid;
          }

          .amount {
            align-self: end;
          }
          """;

    /// <summary>
    /// The grid defect in a package whose sample cannot show it: every row is shorter
    /// than the page the manifest declares, so a render of the sample would print no
    /// split row and no blank page.
    /// </summary>
    public static Dictionary<string, byte[]> GridBottomSampleInvisible()
    {
        var package = GridBottom("scriban");
        package["sample.json"] = Encoding.UTF8.GetBytes(SampleInvisible);
        package["html/styles.css"] = Encoding.UTF8.GetBytes(SampleInvisibleStylesheet);
        return package;
    }

    /// <summary>The records the sample of <see cref="GridBottomSampleInvisible"/> holds.</summary>
    public const int SampleInvisibleRecordCount = 3;

    /// <summary>The height <see cref="SampleInvisibleStylesheet"/> gives every row, in millimetres.</summary>
    public const int SampleInvisibleRowHeightMm = 6;

    /// <summary>
    /// The stylesheet of <see cref="GridBottomSampleInvisible"/>: a fixed row height
    /// beside the bottom alignment, so the row cannot be taller than one page however
    /// the data reads.
    /// </summary>
    public const string SampleInvisibleStylesheet = """
        body {
          margin: 0;
          font-family: "Liberation Sans", sans-serif;
        }

        .grid {
          display: grid;
        }

        .amount {
          height: 6mm;
          align-self: end;
        }
        """;

    /// <summary>The sample of <see cref="GridBottomSampleInvisible"/>: three short records.</summary>
    public const string SampleInvisible = """
        { "contractVersion": "1.0", "header": { "title": "Invoice" }, "items": [ { "quantity": 1 }, { "quantity": 2 }, { "quantity": 3 } ] }
        """;

    /// <summary>
    /// The package that breaks all three constraints at once: the printed totals
    /// declared on, the cell's family only a generic keyword, and the grid cell
    /// bottom-aligned with no declaration that forbids the break.
    /// </summary>
    /// <param name="engine">The engine the package declares.</param>
    public static Dictionary<string, byte[]> MultiFault(string engine = "scriban")
    {
        var package = GridBottom(engine);
        package["template.yaml"] = Encoding.UTF8.GetBytes(Manifest(engine, "enabled: true"));
        package["html/styles.css"] = Encoding.UTF8.GetBytes(MultiFaultStylesheet(engine));
        return package;
    }

    /// <summary>The stylesheet of <see cref="MultiFault"/>: the generic family and the bottom-aligned grid cell.</summary>
    /// <param name="engine">The engine the package declares.</param>
    public static string MultiFaultStylesheet(string engine) => engine == "liquid"
        ? """
          body {
            margin: 0;
            font-family: "Liberation Sans", sans-serif;
          }

          .grid {
            display: grid;
            align-items: end;
          }

          .amount {
            font-family: sans-serif;
          }
          """
        : """
          body {
            margin: 0;
            font-family: "Liberation Sans", sans-serif;
          }

          .grid {
            display: grid;
          }

          .amount {
            font-family: sans-serif;
            align-self: end;
          }
          """;

    /// <summary>The detail row: a table row whose cell carries its own class.</summary>
    /// <param name="engine">The engine the package declares.</param>
    public static string Row(string engine) => engine switch
    {
        "handlebars" => "<tr><td class=\"cell\">{{ item.quantity }}</td></tr>",
        "liquid" => "<tr><td class=\"cell\">{{ item.quantity }}</td></tr>",
        _ => "<tr><td class=\"cell\">{{ item.quantity | html.escape }}</td></tr>",
    };

    /// <summary>The detail text one record prints, encoded the way its engine requires.</summary>
    /// <param name="engine">The engine the package declares.</param>
    public static string Record(string engine) => engine switch
    {
        "handlebars" or "liquid" => "{{ item.quantity }}",
        _ => "{{ item.quantity | html.escape }}",
    };

    /// <summary>The stylesheet: the family named on the cell's own rule, with a generic fallback.</summary>
    public const string Stylesheet = """
        body {
          margin: 0;
        }

        .cell {
          font-family: "Liberation Sans", sans-serif;
        }
        """;

    /// <summary>The schema every package here declares.</summary>
    public const string Schema = """
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

    /// <summary>The sample every package here carries.</summary>
    public const string Sample = """
        { "contractVersion": "1.0", "header": { "title": "Invoice" }, "items": [ { "quantity": 1 } ] }
        """;

    /// <summary>
    /// The manifest: the PDF output declared, the totals off, the anchors and the
    /// owner the take's structural rules require, and an engine version this
    /// deployment runs.
    /// </summary>
    /// <param name="engine">The engine the package declares.</param>
    /// <param name="pageTotals">The totals line to write, or <c>enabled: null</c> to leave the member out.</param>
    public static string Manifest(string engine, string pageTotals)
    {
        var extension = Extension(engine);
        var totals = pageTotals == "enabled: null"
            ? string.Empty
            : "\n    pageTotals:\n      " + pageTotals;
        return $"""
        manifestVersion: "1.0"
        id: {TemplateId}
        version: "{Version}"
        owner: {Owner}
        shared: false
        engine: {engine}
        engineVersion: "{EngineVersion(engine)}"
        dataContract:
          version: "1.0"
          profile: header-items-v1
          schema: schema.json
        outputs:
          html:
            parts:
              documentStart: html/document-start.html{extension}
              row: html/row.html{extension}
              documentEnd: html/document-end.html{extension}
            styles: [html/styles.css]
          pdf:
            paper: A4
            orientation: portrait
            marginMm:
              top: 15
              right: 12
              bottom: 18
              left: 12{totals}
            verification:
              anchors:
                header: ["$.title"]
                item: ["$.quantity"]
        title: "Invoice"
        """;
    }

    /// <summary>
    /// A package that declares the printed page totals with the accounting contract: a
    /// grid row with one root element and an amount cell marked <c>data-role="amount"</c>,
    /// a group-end part whose total cell is marked <c>data-role="group-total"</c>, a
    /// declared grouping, and the reserved band inside the bottom margin with the three
    /// labels. It is the package the accounting admission rules admit.
    /// </summary>
    /// <param name="engine">The engine the package declares.</param>
    public static Dictionary<string, byte[]> Accounted(string engine)
    {
        var extension = Extension(engine);
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["template.yaml"] = Encoding.UTF8.GetBytes(AccountedManifest(engine)),
            ["schema.json"] = Encoding.UTF8.GetBytes(AccountedSchema),
            ["sample.json"] = Encoding.UTF8.GetBytes(AccountedSample),
            ["html/document-start.html" + extension] = Encoding.UTF8.GetBytes("<html><head><link rel=\"stylesheet\" href=\"html/styles.css\"></head><body><div class=\"doc\">"),
            ["html/group-start.html" + extension] = Encoding.UTF8.GetBytes("<div class=\"group\"><div class=\"group-key\">" + Escape(engine, "group.key") + "</div>"),
            ["html/row.html" + extension] = Encoding.UTF8.GetBytes(
                "<div class=\"item\"><div class=\"seq\">" + Escape(engine, "block.recordOrdinal")
                + "</div><div class=\"desc\">" + Escape(engine, "item.description")
                + "</div><div class=\"amount\" data-role=\"amount\">" + Escape(engine, "item.amount") + "</div></div>"),
            ["html/group-end.html" + extension] = Encoding.UTF8.GetBytes(
                "<div class=\"group-total\">" + Escape(engine, "group.key") + "<span data-role=\"group-total\"></span></div></div>"),
            ["html/document-end.html" + extension] = Encoding.UTF8.GetBytes("</div><div class=\"end\">End</div></body></html>"),
            ["html/styles.css"] = Encoding.UTF8.GetBytes(AccountedStylesheet),
        };
    }

    /// <summary>The manifest of <see cref="Accounted"/>: the totals on with their accounting contract and the grouping.</summary>
    /// <param name="engine">The engine the package declares.</param>
    public static string AccountedManifest(string engine) => $"""
        manifestVersion: "1.0"
        id: {TemplateId}
        version: "{Version}"
        owner: {Owner}
        shared: false
        engine: {engine}
        engineVersion: "{EngineVersion(engine)}"
        dataContract:
          version: "1.0"
          profile: header-items-v1
          schema: schema.json
        outputs:
          html:
            parts:
              documentStart: html/document-start.html{Extension(engine)}
              groupStart: html/group-start.html{Extension(engine)}
              row: html/row.html{Extension(engine)}
              groupEnd: html/group-end.html{Extension(engine)}
              documentEnd: html/document-end.html{Extension(engine)}
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
              amountField: amount
              scale: 2
              footer:
                leftMm: 12
                topMm: 282
                widthMm: 186
                heightMm: 12
                labels:
                  broughtForward: "Saldo anterior"
                  pageSubtotal: "Subtotal de pagina"
                  carriedForward: "Saldo siguiente"
            verification:
              anchors:
                header: ["$.title"]
                item: ["$.description"]
        partitioning:
          groupKey: group
        title: "Invoice"
        """;

    /// <summary>The schema of <see cref="Accounted"/>: a description, an amount and a group.</summary>
    public const string AccountedSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "contractVersion": { "type": "string" },
            "header": { "type": "object", "properties": { "title": { "type": "string" } }, "required": ["title"] },
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "description": { "type": "string" },
                  "amount": { "type": "number" },
                  "group": { "type": "string" }
                },
                "required": ["description", "amount", "group"]
              }
            }
          },
          "required": ["contractVersion", "header", "items"]
        }
        """;

    /// <summary>The sample of <see cref="Accounted"/>: two groups and two amounts.</summary>
    public const string AccountedSample = """
        { "contractVersion": "1.0", "header": { "title": "Invoice" }, "items": [ { "description": "First", "amount": 10.00, "group": "A" }, { "description": "Second", "amount": 2.50, "group": "B" } ] }
        """;

    /// <summary>The stylesheet of <see cref="Accounted"/>: the named family on the cells, a grid row of four columns.</summary>
    public const string AccountedStylesheet = """
        body {
          margin: 0;
          font-family: "Liberation Sans", sans-serif;
        }

        .item {
          display: grid;
          grid-template-columns: 12mm 1fr 30mm;
          break-inside: avoid;
        }

        .item > div,
        .group-total {
          font-family: "Liberation Sans", sans-serif;
        }
        """;

    /// <summary>The escape expression of one engine.</summary>
    private static string Escape(string engine, string path) => engine switch
    {
        "handlebars" => "{{ " + path + " }}",
        "liquid" => "{{ " + path + " }}",
        _ => "{{ " + path + " | html.escape }}",
    };

    private static Dictionary<string, byte[]> With(this Dictionary<string, byte[]> package, string path, string text)
    {
        package[path] = Encoding.UTF8.GetBytes(text);
        return package;
    }
}
