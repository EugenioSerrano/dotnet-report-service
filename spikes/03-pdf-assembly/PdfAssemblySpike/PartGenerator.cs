using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfAssemblySpike;

/// <summary>
/// Generates a seeded set of synthetic partial PDFs and its manifest. Every page
/// carries a heading, a marker naming its part and page, the part's logo, text
/// and a table — and leaves the footer band empty for the page number.
/// </summary>
internal static class PartGenerator
{
    public const ulong DefaultSeed = 20261021;
    public const int MinPagesPerPart = 10;
    public const int MaxPagesPerPart = 30;

    private static readonly string[] Words =
    [
        "account", "amount", "balance", "branch", "budget", "cash", "client", "cost", "credit", "customer",
        "debit", "delivery", "deposit", "detail", "entry", "estimate", "expense", "export", "figure", "forecast",
        "income", "invoice", "item", "ledger", "line", "margin", "month", "net", "order", "payment",
        "period", "price", "profit", "quarter", "quantity", "rate", "receipt", "region", "revenue", "sale",
        "service", "share", "statement", "stock", "summary", "supplier", "tax", "total", "transfer", "value",
        "volume", "week", "year", "report", "group", "section", "record", "column", "result", "review",
    ];

    // Fixed document dates (and document IDs below). They do not make the bytes
    // reproducible: the library still writes a random metadata document and
    // instance ID and a random font-subset tag on every save, so two generations
    // are compared with those two fields masked.
    private static readonly DateTime FixedDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static int PartsFor(int targetPages) => targetPages switch
    {
        1000 => 50,
        5000 => 250,
        10000 => 500,
        _ => Math.Max(1, targetPages / 20),
    };

    /// <summary>
    /// Generates the set and returns the manifest path; throws when a file does not
    /// have its page count or its random fields do not have the expected shape.
    /// </summary>
    public static string Generate(int targetPages, ulong seed, string outDir, IReadOnlyList<int>? fixedPageCounts = null)
    {
        Directory.CreateDirectory(outDir);
        var setRandom = new SplitMix64(seed ^ ((ulong)targetPages * 0xD6E8FEB86659FD93UL));
        int[] counts = fixedPageCounts?.ToArray() ?? PageCounts(targetPages, PartsFor(targetPages), setRandom);
        if (counts.Sum() != targetPages)
        {
            throw new InvalidOperationException($"Page counts add up to {counts.Sum()}, not {targetPages}.");
        }

        var parts = new List<PartEntry>(counts.Length);
        int nextGlobal = 1;
        for (int i = 0; i < counts.Length; i++)
        {
            int order = i + 1;
            string file = FileName(seed, targetPages, order);
            var partRandom = new SplitMix64(setRandom.NextUInt64());
            string path = Path.Combine(outDir, file);
            WritePart(path, order, counts[i], partRandom);
            parts.Add(new PartEntry(order, file, counts[i], nextGlobal, string.Empty));
            nextGlobal += counts[i];
        }

        // Re-read every file and check its page count before the manifest is written.
        for (int i = 0; i < parts.Count; i++)
        {
            string path = Path.Combine(outDir, parts[i].File);
            using (var check = PdfReader.Open(path, PdfDocumentOpenMode.Import))
            {
                if (check.PageCount != parts[i].Pages)
                {
                    throw new InvalidDataException($"{parts[i].File} has {check.PageCount} pages, expected {parts[i].Pages}.");
                }
            }

            byte[] bytes = File.ReadAllBytes(path);
            var masked = MaskedComparison.Analyze(bytes);
            if (masked.MaskedSha256 is null)
            {
                throw new InvalidDataException($"{parts[i].File}: {masked.ShapeFailure} — {masked.ShapeDetail}.");
            }

            parts[i] = parts[i] with { Sha256 = masked.Sha256, MaskedSha256 = masked.MaskedSha256 };
        }

        var manifest = new SetManifest(seed, targetPages, parts);
        string manifestPath = Path.Combine(outDir, "manifest.json");
        manifest.Save(manifestPath);
        return manifestPath;
    }

    /// <summary>Draws 10–30 pages per part, then adjusts single parts until the sum is exactly the target.</summary>
    public static int[] PageCounts(int targetPages, int partCount, SplitMix64 random)
    {
        if (targetPages < partCount * MinPagesPerPart || targetPages > partCount * MaxPagesPerPart)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPages), $"{targetPages} pages cannot be split into {partCount} parts of {MinPagesPerPart}-{MaxPagesPerPart} pages.");
        }

        int[] counts = new int[partCount];
        for (int i = 0; i < partCount; i++)
        {
            counts[i] = random.NextInt(MinPagesPerPart, MaxPagesPerPart + 1);
        }

        int sum = counts.Sum();
        while (sum != targetPages)
        {
            int index = random.NextInt(0, partCount);
            if (sum < targetPages && counts[index] < MaxPagesPerPart)
            {
                counts[index]++;
                sum++;
            }
            else if (sum > targetPages && counts[index] > MinPagesPerPart)
            {
                counts[index]--;
                sum--;
            }
        }

        return counts;
    }

    public static string FileName(ulong seed, int targetPages, int order)
    {
        string key = string.Create(CultureInfo.InvariantCulture, $"part|{seed}|{targetPages}|{order}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".pdf";
    }

    public static string Marker(int part, int page) =>
        string.Create(CultureInfo.InvariantCulture, $"[[P{part:D4}.{page:D3}]]");

    private static void WritePart(string path, int order, int pageCount, SplitMix64 random)
    {
        byte[] logoBytes = BmpWriter.CreateLogo(order, random);
        using var logoStream = new MemoryStream(logoBytes, writable: false);
        using var document = new PdfDocument();
        document.Info.Title = string.Create(CultureInfo.InvariantCulture, $"Synthetic report part {order:D4}");
        document.Info.CreationDate = FixedDate;
        document.Info.ModificationDate = FixedDate;
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(path))))[..32];
        document.Internals.FirstDocumentID = id;
        document.Internals.SecondDocumentID = id;
        using var logo = XImage.FromStream(logoStream);
        for (int k = 1; k <= pageCount; k++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromMillimeter(210);
            page.Height = XUnit.FromMillimeter(297);
            using var gfx = XGraphics.FromPdfPage(page);
            DrawPage(gfx, order, k, logo, random);
        }

        document.Save(path);
    }

    private static void DrawPage(XGraphics gfx, int order, int pageInPart, XImage logo, SplitMix64 random)
    {
        const double left = 40;
        double right = PageLayout.PageWidth - 40;
        double logoSize = XUnit.FromMillimeter(20).Point;
        gfx.DrawImage(logo, right - logoSize, 28, logoSize, logoSize);
        gfx.DrawString(string.Create(CultureInfo.InvariantCulture, $"Synthetic report part {order:D4}"), PageLayout.Heading, XBrushes.Black, left, 48);
        gfx.DrawString(Marker(order, pageInPart), PageLayout.Marker, XBrushes.Black, left, 66);

        double y = 112;
        int paragraphs = random.NextInt(2, 4);
        double width = right - left;
        for (int p = 0; p < paragraphs; p++)
        {
            int words = random.NextInt(40, 71);
            var line = new StringBuilder();
            for (int w = 0; w < words; w++)
            {
                string word = Words[random.NextInt(0, Words.Length)];
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && gfx.MeasureString(candidate, PageLayout.Body).Width > width)
                {
                    gfx.DrawString(line.ToString(), PageLayout.Body, XBrushes.Black, left, y);
                    y += 13;
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }

            gfx.DrawString(line.ToString(), PageLayout.Body, XBrushes.Black, left, y);
            y += 22;
        }

        // Table grid: 12 rows x 4 columns of numbers with two decimals.
        const int rows = 12;
        const int columns = 4;
        const double rowHeight = 18;
        double columnWidth = width / columns;
        double top = y;
        var pen = new XPen(XColors.Black, 0.5);
        for (int r = 0; r <= rows; r++)
        {
            gfx.DrawLine(pen, left, top + (r * rowHeight), right, top + (r * rowHeight));
        }

        for (int c = 0; c <= columns; c++)
        {
            gfx.DrawLine(pen, left + (c * columnWidth), top, left + (c * columnWidth), top + (rows * rowHeight));
        }

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                double value = random.NextInt(0, 10_000_000) / 100.0;
                var cell = new XRect(left + (c * columnWidth) + 4, top + (r * rowHeight), columnWidth - 8, rowHeight);
                gfx.DrawString(value.ToString("F2", CultureInfo.InvariantCulture), PageLayout.Table, XBrushes.Black, cell, XStringFormats.CenterRight);
            }
        }

        double bottom = top + (rows * rowHeight) + 0.5;
        if (bottom > PageLayout.ContentBottomLimit)
        {
            throw new InvalidOperationException($"Content reaches {bottom:F1} pt, below the limit {PageLayout.ContentBottomLimit:F1} pt.");
        }
    }
}
