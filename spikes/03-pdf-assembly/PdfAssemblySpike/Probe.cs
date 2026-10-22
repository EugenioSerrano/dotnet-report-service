using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfAssemblySpike;

/// <summary>
/// Pre-flight checks of library behaviour the measurements rely on, on a set of
/// two parts of three pages: text extraction of the embedded fonts, what
/// appending a page number adds to a page, closing sources after import, and
/// drawing a bitmap logo.
/// </summary>
internal static partial class Probe
{
    public static int Run(string outDir, string? fontDir)
    {
        DejaVuFontResolver.Install(fontDir);
        Directory.CreateDirectory(outDir);
        string setDir = Path.Combine(outDir, "probe-set");
        if (Directory.Exists(setDir))
        {
            Directory.Delete(setDir, true);
        }

        string manifestPath = PartGenerator.Generate(6, PartGenerator.DefaultSeed, setDir, [3, 3]);
        var manifest = SetManifest.Load(manifestPath);
        var report = new Dictionary<string, object?>
        {
            ["fontEncodingOption"] = PageLayout.Encoding.ToString(),
            ["stampWrappedInGraphicsState"] = PageStamper.WrapInGraphicsState,
            ["sourcesKeptOpenUntilSave"] = Assembler.KeepSourcesOpenUntilSave,
        };
        bool pass = true;

        // (a) Text extraction of a merged and numbered output.
        string stamped = Path.Combine(outDir, "probe-merge-stamp.pdf");
        Assembler.MergeParts(manifest, manifestPath, stamped, stamp: true);
        string text = ExternalTool.Output(Verifier.PdfToText, "-enc", "UTF-8", "-layout", stamped, "-");
        string[] pages = text.Split('\f');
        var perPage = new List<string>();
        bool textOk = true;
        var map = manifest.PageMap();
        for (int i = 1; i <= 6; i++)
        {
            string page = i <= pages.Length ? pages[i - 1] : string.Empty;
            int markers = Regex.Count(page, Regex.Escape(PartGenerator.Marker(map[i - 1].Part, map[i - 1].Page)));
            int anyMarkers = Regex.Count(page, @"\[\[P\d{4}\.\d{3}\]\]");
            int numbers = Regex.Count(page, Regex.Escape(PageStamper.Text(i, 6)));
            perPage.Add(string.Create(CultureInfo.InvariantCulture, $"page {i}: marker {markers}/{anyMarkers}, number {numbers}"));
            textOk &= markers == 1 && anyMarkers == 1 && numbers == 1;
        }

        string qdf = Path.Combine(outDir, "probe-merge-stamp.qdf.pdf");
        ExternalTool.Run(Verifier.Qpdf, ["--qdf", "--object-streams=disable", stamped, qdf]);
        string qdfText = Encoding.Latin1.GetString(File.ReadAllBytes(qdf));
        report["a_textExtraction"] = new
        {
            pass = textOk,
            perPage,
            toUnicodeEntries = Regex.Count(qdfText, "/ToUnicode"),
            type0Fonts = Regex.Count(qdfText, @"/Subtype\s*/Type0"),
            trueTypeFonts = Regex.Count(qdfText, @"/Subtype\s*/TrueType"),
            identityH = Regex.Count(qdfText, "/Identity-H"),
            winAnsi = Regex.Count(qdfText, "/WinAnsiEncoding"),
            fontNames = FontNamesPattern().Matches(qdfText).Select(m => m.Groups[1].Value).Distinct().ToList(),
        };
        pass &= textOk;

        // (b) What appending the page number adds to a page.
        string part1 = SetManifest.PartPath(manifestPath, manifest.Parts[0]);
        var sourceStreams = ContentStreams(part1, 1);
        var stampedStreams = ContentStreams(stamped, 1);
        var streamSummaries = stampedStreams.Select(n => Summarize(stamped, n)).ToList();
        string firstData = StreamData(stamped, stampedStreams[0]).Trim();
        string lastOriginal = stampedStreams.Count >= 2 ? StreamData(stamped, stampedStreams[^2]).TrimEnd() : string.Empty;
        string probeDir = Path.Combine(outDir, "probe-render");
        Directory.CreateDirectory(probeDir);
        var reference = Verifier.Render(part1, 1, 1, Path.Combine(probeDir, "source"));
        var candidate = Verifier.Render(stamped, 1, 1, Path.Combine(probeDir, "stamped"));
        var diff = PgmImage.Compare(PgmImage.Load(reference[1]), PgmImage.Load(candidate[1]), PageLayout.PageHeight, PageLayout.FooterBandHeight);
        bool appendOk = diff.SameSize && diff.ChangedOutside == 0 && diff.ChangedInside >= 1;
        report["b_append"] = new
        {
            pass = appendOk,
            sourceContentStreams = sourceStreams.Count,
            sourceStreams = sourceStreams.Select(n => Summarize(part1, n)).ToList(),
            stampedContentStreams = stampedStreams.Count,
            stampedStreams = streamSummaries,
            originalWrappedInSaveRestore = firstData == "q" && lastOriginal.EndsWith('Q'),
            changedOutsideBand = diff.ChangedOutside,
            changedInsideBand = diff.ChangedInside,
            bandTopRow = diff.BandTopRow,
        };
        pass &= appendOk;

        // (c) Closing each source right after its pages were added.
        var check = QpdfCheck.Run(stamped);
        var checkSource = QpdfCheck.Run(part1);
        bool collectable = SourceCollectableAfterDispose(part1);
        string merged = Path.Combine(outDir, "probe-merge.pdf");
        Assembler.MergeParts(manifest, manifestPath, merged, stamp: false);
        var checkMerged = QpdfCheck.Run(merged);
        bool disposeOk = check.Pass && checkMerged.Pass && checkSource.Pass;
        report["c_sourceDisposal"] = new
        {
            pass = disposeOk,
            exemptMessage = QpdfCheck.ExemptTail,
            qpdfCheckStamped = check,
            qpdfCheckMerged = checkMerged,
            qpdfCheckSourcePart = checkSource,
            sourceCollectableWhileOutputOpen = collectable,
        };
        pass &= disposeOk;

        // (d) Bitmap logo drawn on the page (top right, 20 mm square).
        var source = PgmImage.Load(reference[1]);
        double logoSize = XUnit.FromMillimeter(20).Point;
        int logoLeft = (int)Math.Round(PageLayout.PageWidth - 40 - logoSize);
        long logoPixels = source.NonWhite(logoLeft + 2, 30, (int)logoSize - 4, (int)logoSize - 4);
        long areaPixels = ((int)logoSize - 4) * (long)((int)logoSize - 4);
        bool logoOk = logoPixels > areaPixels / 2;
        report["d_bitmapLogo"] = new { pass = logoOk, bitsPerPixel = 24, nonWhitePixelsInLogoArea = logoPixels, logoAreaPixels = areaPixels };
        pass &= logoOk;

        Directory.Delete(probeDir, true);
        report["pass"] = pass;
        ResultsWriter.WriteJson(Path.Combine(outDir, "probe.json"), report);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, SetManifest.Json));
        Console.WriteLine($"probe: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SourceCollectableAfterDispose(string path)
    {
        using var output = new PdfDocument();
        var weak = ImportAndClose(output, path);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        bool collectable = !weak.IsAlive;
        output.Save(new MemoryStream());
        return collectable;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ImportAndClose(PdfDocument output, string path)
    {
        var source = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        for (int k = 0; k < source.PageCount; k++)
        {
            output.AddPage(source.Pages[k]);
        }

        var weak = new WeakReference(source);
        source.Dispose();
        return weak;
    }

    /// <summary>Object numbers of the content streams of one page, from "qpdf --show-pages".</summary>
    private static List<int> ContentStreams(string pdf, int page)
    {
        string listing = ExternalTool.Output(Verifier.Qpdf, "--show-pages", pdf);
        var result = new List<int>();
        bool inPage = false;
        foreach (string raw in listing.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("page ", StringComparison.Ordinal))
            {
                inPage = line.StartsWith(string.Create(CultureInfo.InvariantCulture, $"page {page}:"), StringComparison.Ordinal);
                continue;
            }

            var match = ObjectReferencePattern().Match(line);
            if (inPage && match.Success)
            {
                result.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }

        return result;
    }

    /// <summary>Decoded data of one stream; qpdf warnings (exit 3) are tolerated here because this only describes the stream.</summary>
    private static string StreamData(string pdf, int objectNumber)
    {
        var result = ExternalTool.Run(Verifier.Qpdf, [string.Create(CultureInfo.InvariantCulture, $"--show-object={objectNumber}"), "--filtered-stream-data", pdf]);
        if (result.ExitCode is not (0 or 3))
        {
            throw new InvalidOperationException($"qpdf could not decode object {objectNumber}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    private static string Summarize(string pdf, int objectNumber)
    {
        string data = StreamData(pdf, objectNumber).Replace('\n', ' ').Trim();
        string head = data.Length > 60 ? data[..60] + " ..." : data;
        string tail = data.Length > 60 ? " ... " + data[^40..] : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"object {objectNumber}, {data.Length} chars: {head}{tail}");
    }

    [GeneratedRegex(@"^\s+(\d+) 0 R$")]
    private static partial Regex ObjectReferencePattern();

    [GeneratedRegex(@"/BaseFont\s*/([A-Za-z0-9+\-]+)")]
    private static partial Regex FontNamesPattern();
}
