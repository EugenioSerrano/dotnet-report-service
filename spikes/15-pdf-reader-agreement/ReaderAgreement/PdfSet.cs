namespace ReaderAgreement;

/// <summary>
/// One PDF of the measured set. Judged PDFs carry a recorded reference reading
/// (the earlier checker's entry for their run) and the parameters the rules
/// need; agreement-only PDFs are compared on words, pages and positions only.
/// </summary>
internal sealed record PdfEntry
{
    public required string Id { get; init; }

    /// <summary>Path from the repository root; null for a regenerated PDF until it exists (see regenerated.json).</summary>
    public string? Source { get; init; }

    public required string Folder { get; init; }

    public required string Class { get; init; }

    /// <summary>judged | agreement.</summary>
    public required string Use { get; init; }

    /// <summary>paged | native | native-two-pass | assembly | layout-dom.</summary>
    public required string Layout { get; init; }

    public string? FixtureId { get; init; }

    public string? Markup { get; init; }

    public string Marks { get; init; } = "none";

    public string Footer { get; init; } = "none";

    /// <summary>The earlier experiment folder whose results hold the reference entry.</summary>
    public string? RecordFolder { get; init; }

    public string? RecordRunId { get; init; }

    /// <summary>Another folder's strict-accounting entry of the same run content, when this PDF's own folder has none.</summary>
    public string? StrictRecordFolder { get; init; }

    public string? StrictRecordRunId { get; init; }

    /// <summary>Judged rules this PDF must fail (a known-bad PDF), e.g. TI-2, S1.</summary>
    public string[] KnownBad { get; init; } = [];

    public bool Regenerated { get; init; }

    /// <summary>For a regenerated PDF: the experiment folder whose harness writes it.</summary>
    public string? Harness { get; init; }

    public bool Accounting => Footer == "accounting";
}

/// <summary>The fixed set of 42 PDFs: 20 committed and judged, 10 regenerated and judged, 12 compared only.</summary>
internal static class PdfSet
{
    private const string L07 = "spikes/07-native-tall-rows/results/pdf/";
    private const string L08 = "spikes/08-pagedjs-pre-split/results/pdf/";
    private const string L09 = "spikes/09-two-pass-page-subtotals/results/pdf/";
    private const string L10 = "spikes/10-pagedjs-table-strict-accounting/results/pdf/";
    private const string L12 = "spikes/12-group-continuity/results/pdf/";
    private const string F07 = "07-native-tall-rows";
    private const string F08 = "08-pagedjs-pre-split";
    private const string F09 = "09-two-pass-page-subtotals";
    private const string F10 = "10-pagedjs-table-strict-accounting";
    private const string F12 = "12-group-continuity";
    private const string Split = "-split-t1.00-f0.90-on-r1";

    public static IReadOnlyList<PdfEntry> All { get; } = Build();

    private static List<PdfEntry> Build()
    {
        var set = new List<PdfEntry>();

        // Native layout, committed (earlier native experiment): no marks, no footer.
        void Native07(string run, string fixture, string markup, string cls) => set.Add(new PdfEntry
        {
            Id = $"07/{run}", Source = L07 + run + ".pdf", Folder = F07, Class = cls, Use = "judged", Layout = "native",
            FixtureId = fixture, Markup = markup, RecordFolder = F07, RecordRunId = run,
        });
        Native07("B1000-table-r1", "B1000", "table", "ordinary block");
        Native07("T300u-table-r1", "T300u", "table", "tall row split into parts");
        Native07("T300u-ab-grid-r1", "T300u-ab", "grid", "tall row split into parts");
        Native07("X64K-ab-grid-r1", "X64K-ab", "grid", "tall row split into parts");

        // Paged.js grid with the pre-split, committed; the footer is on (accounting).
        void Paged08(string file, string fixture, string cls, string[] knownBad, string? strictFolder = null, string? strictRun = null) => set.Add(new PdfEntry
        {
            Id = $"08/s2-{file}", Source = L08 + file + ".pdf", Folder = F08, Class = cls, Use = "judged", Layout = "paged",
            FixtureId = fixture, Markup = "grid", Marks = "expected", Footer = "accounting",
            RecordFolder = F08, RecordRunId = "s2-" + file, KnownBad = knownBad,
            StrictRecordFolder = strictFolder, StrictRecordRunId = strictRun,
        });
        Paged08("G200-off-on-r1", "G200", "known-bad: lost rows (no split)", ["TI-2", "S1", "S2", "S3"], F10, "s1-grid-s2-G200-off-on-r1");
        Paged08("G200" + Split, "G200", "tall row split into parts", []);
        Paged08("W2a" + Split, "W2a", "tall row split into parts", []);
        Paged08("X1M" + Split, "X1M", "tall row split into parts; largest Paged.js PDF", []);

        // Native two-pass: pass 2 carries stamped marks and the accounting footer; pass 1 in the "cell" strategy carries neither.
        void TwoPass(string run, string fixture, string markup, bool second, string cls) => set.Add(new PdfEntry
        {
            Id = $"09/{run}", Source = L09 + run + ".pdf", Folder = F09, Class = cls, Use = "judged", Layout = "native-two-pass",
            FixtureId = fixture, Markup = markup, Marks = second ? "expected" : "none", Footer = second ? "accounting" : "none",
            RecordFolder = F09, RecordRunId = run,
        });
        TwoPass("B100-grid-s-r1.p2", "B100", "grid", true, "ordinary block");
        TwoPass("T300u-grid-s-r1.p2", "T300u", "grid", true, "tall row split into parts");
        TwoPass("T300u-ab-grid-c-r1.p1", "T300u-ab", "grid", false, "tall row split into parts");
        TwoPass("T300u-table-c-r1.p1", "T300u", "table", false, "tall row split into parts");

        // Paged.js with the pre-split in grid and table markup, committed; footer on.
        void Paged10(string run, string fixture, string markup, string cls, string[] knownBad) => set.Add(new PdfEntry
        {
            Id = $"10/{run}", Source = L10 + run + ".pdf", Folder = F10, Class = cls, Use = "judged", Layout = "paged",
            FixtureId = fixture, Markup = markup, Marks = "expected", Footer = "accounting",
            RecordFolder = F10, RecordRunId = run, KnownBad = knownBad,
        });
        Paged10("s1-grid-s2-T300u" + Split, "T300u", "grid", "tall row split into parts", []);
        Paged10("s2-table-s1-B1000" + Split, "B1000", "table", "known-bad: lost or altered text (table)", ["TI-3", "TI-4"]);
        Paged10("s2-table-s2-T150" + Split, "T150", "table", "known-bad: lost rows (table)", ["TI-2", "S1", "S2", "S3"]);
        Paged10("s2-table-s2-T300u" + Split, "T300u", "table", "known-bad: lost or altered text (table)", ["TI-3", "TI-4"]);

        // Groups across pages, Paged.js grid, committed; footer on.
        foreach (var fixture in new[] { "GE52", "GE61", "GTF300u", "GTL300u" })
        {
            var run = $"s1-grid-new-{fixture}" + Split;
            set.Add(new PdfEntry
            {
                Id = $"12/{run}", Source = L12 + run + ".pdf", Folder = F12, Class = "groups across pages", Use = "judged", Layout = "paged",
                FixtureId = fixture, Markup = "grid", Marks = "expected", Footer = "accounting", RecordFolder = F12, RecordRunId = run,
            });
        }

        // Regenerated with the original harnesses (missing from the committed results).
        void Regen07(string run, string fixture, string cls, string[] knownBad) => set.Add(new PdfEntry
        {
            Id = $"R/07/{run}", Folder = F07, Class = cls, Use = "judged", Layout = "native",
            FixtureId = fixture, Markup = "table", RecordFolder = F07, RecordRunId = run, KnownBad = knownBad,
            Regenerated = true, Harness = F07,
        });
        Regen07("NC-W-B100-table-r1", "B100", "known-bad: word dropped", ["TI-3"]);
        Regen07("NC-L-B100-table-r1", "B100", "known-bad: line repeated", ["TI-3"]);
        Regen07("NC-R-B100-table-r1", "B100", "known-bad: row lost", ["TI-2"]);
        Regen07("NC-F-F26K-a-table-r1", "F26K-a", "known-bad: free-text word dropped", ["TI-6"]);
        Regen07("F1M-a-table-r1", "F1M-a", "long free text", []);
        Regen07("X1M-table-r1", "X1M", "largest native table", []);

        void Regen10(string run, string fixture, string cls, string[] knownBad) => set.Add(new PdfEntry
        {
            Id = $"R/10/{run}", Folder = F10, Class = cls, Use = "judged", Layout = "paged",
            FixtureId = fixture, Markup = "grid", Marks = "expected", Footer = "accounting",
            RecordFolder = F10, RecordRunId = run, KnownBad = knownBad, Regenerated = true, Harness = F10,
        });
        Regen10("s1-grid-s1-B1000" + Split, "B1000", "ordinary block", []);
        Regen10("s1-grid-s1-NC-R-B100" + Split, "B100", "known-bad: row lost", ["TI-2", "S1", "S3"]);
        Regen10("s1-grid-s1-NC-W-B100" + Split, "B100", "known-bad: word dropped", ["TI-3"]);
        Regen10("s1-grid-s2-F1M-a" + Split, "F1M-a", "long free text", []);

        // Compared on words, pages and positions only: no reference verdict from a PDF reading exists for them.
        void Only(string id, string source, string folder, string layout, string cls) => set.Add(new PdfEntry
        {
            Id = id, Source = source, Folder = folder, Class = cls, Use = "agreement", Layout = layout,
        });
        const string L02 = "spikes/02-pdf-layout/results/pdf/";
        Only("02/native-table-B1000-r1", L02 + "native-table-B1000-r1.pdf", "02-pdf-layout", "layout-dom", "ordinary block (checked on the page, not the PDF)");
        Only("02/paged-grid-keep-G200-on-r1", L02 + "paged-grid-keep-G200-on-r1.pdf", "02-pdf-layout", "layout-dom", "giant rows, rows kept whole");
        Only("02/paged-table-keep-B1000-on-r1", L02 + "paged-table-keep-B1000-on-r1.pdf", "02-pdf-layout", "layout-dom", "ordinary block, rows kept whole");
        Only("02/paged-table-keep-G200-on-r1", L02 + "paged-table-keep-G200-on-r1.pdf", "02-pdf-layout", "layout-dom", "giant rows, rows kept whole");
        Only("03/partial-part-0001", "spikes/03-pdf-assembly/results/samples/partial-part-0001.pdf", "03-pdf-assembly", "assembly", "assembled part (also 06 fixtures/pdfsharp-part.pdf, byte-identical)");
        Only("03/three-parts-numbered", "spikes/03-pdf-assembly/results/samples/three-parts-numbered.pdf", "03-pdf-assembly", "assembly", "assembled and stamped");
        Only("06/partial-part-0001", "spikes/06-pdf-assembly-chromium-parts/results/samples/partial-part-0001.pdf", "06-pdf-assembly-chromium-parts", "assembly", "assembled part");
        Only("06/three-parts-numbered", "spikes/06-pdf-assembly-chromium-parts/results/samples/three-parts-numbered.pdf", "06-pdf-assembly-chromium-parts", "assembly", "assembled and stamped");
        Only("09/B100-grid-s-r1.p1", L09 + "B100-grid-s-r1.p1.pdf", F09, "native-two-pass", "first pass, no reference entry");
        Only("09/T300u-grid-s-r1.p1", L09 + "T300u-grid-s-r1.p1.pdf", F09, "native-two-pass", "first pass, no reference entry");
        Only("11/U066-table-c-r1.p1", "spikes/11-row-end-link-threshold/results/pdf/U066-table-c-r1.p1.pdf", "11-row-end-link-threshold", "native-two-pass", "row-end link sweep");
        Only("11/X64K-table-c-r1.p1", "spikes/11-row-end-link-threshold/results/pdf/X64K-table-c-r1.p1.pdf", "11-row-end-link-threshold", "native-two-pass", "row-end link sweep");
        return set;
    }

    /// <summary>The PDF file of an entry: its committed source, or the regenerated file recorded in regenerated.json.</summary>
    public static string? PathOf(PdfEntry entry, string repositoryRoot, IReadOnlyDictionary<string, RegeneratedPdf>? regenerated)
    {
        if (entry.Source is not null)
        {
            return Path.Combine(repositoryRoot, entry.Source);
        }

        return regenerated is not null && regenerated.TryGetValue(entry.Id, out var r) && r.Path is not null
            ? Path.Combine(repositoryRoot, r.Path)
            : null;
    }
}

/// <summary>A regenerated PDF: where it is, its hash, and whether the reference reading confirmed it is the recorded run.</summary>
internal sealed class RegeneratedPdf
{
    public string Id { get; set; } = string.Empty;

    public string? Path { get; set; }

    public string? Sha256 { get; set; }

    public long? Bytes { get; set; }

    public string? Harness { get; set; }

    public string? HarnessImage { get; set; }
}
