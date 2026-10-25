using System.Diagnostics;
using System.Text.Json;
using PaginationFixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace TwoPassSpike;

/// <summary>
/// Pass 2 ("stamp-one"), one run in a fresh process: open the pass-1 PDF for
/// modification, read the row markers, check them (the guard), compute the page
/// values from the block's source data, stamp them, remove the markers and save.
/// The layout is never re-run. Each step is timed; the whole pass runs within the
/// pass cap. A guard failure writes no PDF: a wrong total is never published.
/// </summary>
internal static class StampRun
{
    public static string PdfSharpVersion => typeof(PdfDocument).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<PassRecord> ExecuteAsync(
        Fixture fixture, string strategy, string? fault, string outDirectory, string pass1Relative, string runId, string fontFile, bool readOnly, TimeSpan cap)
    {
        var record = new PassRecord { PdfSharpVersion = PdfSharpVersion };
        var sampler = new ProcessTreeSampler();
        var watch = Stopwatch.StartNew();
        sampler.Start();
        var work = Task.Run(() => Execute(record, fixture, strategy, fault, outDirectory, pass1Relative, runId, fontFile, readOnly));
        var remaining = cap - watch.Elapsed;
        if (await Task.WhenAny(work, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)) != work)
        {
            record.Outcome = "timeout";
            record.Reason = $"the second pass did not finish within the {cap.TotalSeconds:0} s pass cap";
            record.Pdf = null;
        }
        else
        {
            try
            {
                await work;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                record.Outcome = "error";
                record.Reason = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
                record.Pdf = null;
            }
        }

        record.Memory = sampler.Stop();
        record.TotalMs = watch.Elapsed.TotalMilliseconds;
        record.HarnessVmHwmKiB = ProcessTreeSampler.HarnessHighWaterMarkKiB();
        return record;
    }

    private static void Execute(PassRecord record, Fixture fixture, string strategy, string? fault, string outDirectory, string pass1Relative, string runId, string fontFile, bool readOnly)
    {
        LiberationFontResolver.Install(fontFile);
        var step = Stopwatch.StartNew();
        using var document = PdfReader.Open(Path.Combine(outDirectory, pass1Relative), readOnly ? PdfDocumentOpenMode.Import : PdfDocumentOpenMode.Modify);
        record.OpenMs = step.Elapsed.TotalMilliseconds;
        record.Pages = document.PageCount;

        // Read back, and write the reading before anything is stamped.
        step.Restart();
        var readBack = MarkerReader.Read(document);
        record.ReadMs = step.Elapsed.TotalMilliseconds;
        record.AnnotationsRead = readBack.AnnotationsRead;
        record.MarkerAnnotations = readBack.MarkerAnnotations;
        record.UnknownAnnotations = readBack.UnknownAnnotations;
        var readbackRelative = Path.Combine("readback", runId + ".json");
        File.WriteAllText(Path.Combine(outDirectory, readbackRelative), JsonSerializer.Serialize(Faults.Reported(readBack, fault), Json) + "\n");
        record.Readback = readbackRelative;

        var reasons = MarkerReader.Guard(readBack, [.. fixture.Items.Select(r => r.Sequence!.Value)], strategy);
        if (reasons.Count > 0)
        {
            record.Outcome = "guard-failed";
            record.Reason = string.Join("; ", reasons);
            return;
        }

        if (readOnly)
        {
            // The marker probe: the reading and the guard only; nothing is stamped or saved.
            record.Outcome = "ok";
            record.Reason = "read only (marker probe)";
            return;
        }

        step.Restart();
        var (plan, error) = PageAccounting.Compute(readBack, fixture, strategy);
        if (plan is null)
        {
            record.ComputeMs = step.Elapsed.TotalMilliseconds;
            record.Outcome = "guard-failed";
            record.Reason = error;
            return;
        }

        Faults.Apply(plan, readBack, fault);
        record.ComputeMs = step.Elapsed.TotalMilliseconds;

        step.Restart();
        for (var index = 0; index < document.PageCount; index++)
        {
            var page = index + 1;
            Stamper.StampPage(
                document.Pages[index],
                plan.Pages[index],
                plan.Amounts.Where(a => a.Page == page),
                plan.Squares.Where(s => s.Page == page).Select(s => (s.X, s.Y, s.Side)));
        }

        record.StampMs = step.Elapsed.TotalMilliseconds;

        step.Restart();
        record.MarkersRemoved = MarkerReader.RemoveMarkers(document);
        record.RemoveMs = step.Elapsed.TotalMilliseconds;

        step.Restart();
        var pdfRelative = Path.Combine("pdf", runId + ".p2.pdf");
        var pdfPath = Path.Combine(outDirectory, pdfRelative);
        document.Save(pdfPath);
        record.SaveMs = step.Elapsed.TotalMilliseconds;
        record.PdfBytes = new FileInfo(pdfPath).Length;
        record.Pdf = pdfRelative;

        // For people and debugging only: no checker reads it.
        var stampRelative = Path.Combine("stamp", runId + ".json");
        var stamp = new
        {
            run = runId,
            strategy,
            fault,
            fontFile,
            fontEncoding = Stamper.Encoding.ToString(),
            amountOffsetPt = Stamper.AmountOffsetPt,
            pages = plan.Pages.Select(p =>
            {
                var (left, centre, right) = Stamper.FooterTexts(p);
                return new { page = p.Page, left, centre, right, continues = p.Continues };
            }),
            amounts = plan.Amounts.Select(a => new { seq = a.Seq, page = a.Page, a.Text, baseline = Stamper.BaselineFor(a.Line.YCentre, Stamper.AmountSizePt) }),
            squares = plan.Squares.Select(s => new { page = s.Page, x = s.X, y = s.Y, side = s.Side }),
        };
        File.WriteAllText(Path.Combine(outDirectory, stampRelative), JsonSerializer.Serialize(stamp, Json) + "\n");
        record.Stamp = stampRelative;
        record.Outcome = "ok";
    }
}
