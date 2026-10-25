using System.Globalization;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfAssemblySpike;

/// <summary>
/// The assembly variants measured by the experiment. Each one reads the parts in
/// manifest order and writes one output (or, for stamp-parts, one stamped copy
/// of every part). Runs in its own process so its peak memory is its own.
/// </summary>
internal static class Assembler
{
    public const string Merge = "pdfsharp-merge";
    public const string MergeStamp = "pdfsharp-merge-stamp";
    public const string Staged = "pdfsharp-staged";
    public const string StampParts = "stamp-parts";

    /// <summary>
    /// When true, imported source documents stay open until the output is saved.
    /// Off by default: each source is closed right after its pages were added.
    /// </summary>
    public static readonly bool KeepSourcesOpenUntilSave = false;

    public static int Run(string variant, string manifestPath, string outPath, string tempDir, string metricsPath, int batchSize)
    {
        var manifest = SetManifest.Load(manifestPath);
        Directory.CreateDirectory(tempDir);
        string? outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
        }

        using var sampler = new MemorySampler();
        sampler.Start();
        long outputBytes;
        switch (variant)
        {
            case Merge:
                MergeParts(manifest, manifestPath, outPath, stamp: false);
                outputBytes = new FileInfo(outPath).Length;
                break;
            case MergeStamp:
                MergeParts(manifest, manifestPath, outPath, stamp: true);
                outputBytes = new FileInfo(outPath).Length;
                break;
            case Staged:
                StagedMerge(manifest, manifestPath, outPath, tempDir, batchSize);
                outputBytes = new FileInfo(outPath).Length;
                break;
            case StampParts:
                outputBytes = StampEachPart(manifest, manifestPath, tempDir);
                break;
            default:
                Console.Error.WriteLine($"Unknown variant '{variant}'.");
                return 2;
        }

        var metrics = sampler.Stop(variant, manifest.TargetPages, outputBytes);
        File.WriteAllText(metricsPath, JsonSerializer.Serialize(metrics, SetManifest.Json) + "\n");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"assemble {variant}: {metrics.Pages} pages, {metrics.WorkSeconds:F2} s, heap peak {metrics.ManagedHeapPeakBytes / 1048576.0:F1} MiB, VmHWM {metrics.VmHwmBytes / 1048576.0:F1} MiB"));
        return 0;
    }

    /// <summary>V1 / V2: one output document, parts imported in order, saved once at the end.</summary>
    public static void MergeParts(SetManifest manifest, string manifestPath, string outPath, bool stamp)
    {
        var sources = manifest.Parts.OrderBy(p => p.Order).Select(p => (SetManifest.PartPath(manifestPath, p), p.FirstGlobalPage)).ToList();
        MergeFiles(sources, outPath, stamp ? manifest.TargetPages : 0);
    }

    /// <summary>
    /// Imports every page of every file in the given order into one new document.
    /// When total is greater than 0, each page is stamped with its global number.
    /// </summary>
    public static void MergeFiles(IReadOnlyList<(string Path, int FirstGlobalPage)> files, string outPath, int total)
    {
        var openSources = new List<PdfDocument>();
        using (var output = new PdfDocument())
        {
            foreach (var (path, firstGlobalPage) in files)
            {
                var source = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                for (int k = 0; k < source.PageCount; k++)
                {
                    var page = output.AddPage(source.Pages[k]);
                    if (total > 0)
                    {
                        PageStamper.Stamp(page, firstGlobalPage + k, total);
                    }
                }

                if (KeepSourcesOpenUntilSave)
                {
                    openSources.Add(source);
                }
                else
                {
                    source.Dispose();
                }
            }

            output.Save(outPath);
        }

        foreach (var source in openSources)
        {
            source.Dispose();
        }
    }

    /// <summary>V3: batches of parts merged and stamped into temporary files, then the batch files merged.</summary>
    public static void StagedMerge(SetManifest manifest, string manifestPath, string outPath, string tempDir, int batchSize)
    {
        var ordered = manifest.Parts.OrderBy(p => p.Order).ToList();
        var batchFiles = new List<(string Path, int FirstGlobalPage)>();
        for (int start = 0, batch = 1; start < ordered.Count; start += batchSize, batch++)
        {
            var members = ordered.Skip(start).Take(batchSize)
                .Select(p => (SetManifest.PartPath(manifestPath, p), p.FirstGlobalPage)).ToList();
            string batchPath = Path.Combine(tempDir, string.Create(CultureInfo.InvariantCulture, $"batch-{batch:D3}.pdf"));
            MergeFiles(members, batchPath, manifest.TargetPages);
            batchFiles.Add((batchPath, members[0].Item2));
        }

        MergeFiles(batchFiles, outPath, 0);
    }

    /// <summary>V5 step a: every part opened for modification, stamped with its global numbers and saved into the temp folder.</summary>
    public static long StampEachPart(SetManifest manifest, string manifestPath, string tempDir)
    {
        long bytes = 0;
        foreach (var part in manifest.Parts.OrderBy(p => p.Order))
        {
            string target = Path.Combine(tempDir, part.File);
            using (var document = PdfReader.Open(SetManifest.PartPath(manifestPath, part), PdfDocumentOpenMode.Modify))
            {
                for (int k = 0; k < document.PageCount; k++)
                {
                    PageStamper.Stamp(document.Pages[k], part.FirstGlobalPage + k, manifest.TargetPages);
                }

                document.Save(target);
            }

            bytes += new FileInfo(target).Length;
        }

        return bytes;
    }

    /// <summary>
    /// Writes the qpdf argument file: one argument per line, the parts in
    /// manifest order (taken from partsDir when given, for stamped copies).
    /// </summary>
    public static void WriteQpdfArguments(SetManifest manifest, string manifestPath, string? partsDir, string outputPath, string argumentsPath)
    {
        var lines = new List<string> { "--empty", "--pages" };
        foreach (var part in manifest.Parts.OrderBy(p => p.Order))
        {
            lines.Add(partsDir is null ? SetManifest.PartPath(manifestPath, part) : Path.Combine(Path.GetFullPath(partsDir), part.File));
        }

        lines.Add("--");
        lines.Add(Path.GetFullPath(outputPath));
        File.WriteAllText(argumentsPath, string.Join('\n', lines) + "\n");
    }
}
