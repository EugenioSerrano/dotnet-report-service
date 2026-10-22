using System.Diagnostics;
using System.Runtime;
using System.Text;
using System.Text.Json;
using MarkdownSpike.Input;
using MarkdownSpike.Measuring;
using MarkdownSpike.Rendering;

namespace MarkdownSpike.Routes;

/// <summary>Converts one dataset by one route and prints one JSON result line.</summary>
internal static class ConvertCommand
{
    public const int DefaultBlockSize = 48;

    public static int Run(CommandLine line)
    {
        line.Allow("route", "data", "template", "output", "md-template", "block-size", "whole-threshold", "force-whole", "temp");
        string route = line.Required("route");
        if (route is not ("html" or "a" or "b" or "c" or "d"))
        {
            throw new UsageException("--route must be html, a, b, c or d.");
        }

        var options = new ConvertOptions(
            route,
            line.Required("data"),
            line.Optional("template"),
            line.Required("output"),
            line.Optional("md-template"),
            line.Int("block-size", DefaultBlockSize),
            line.Int("whole-threshold", HybridBySizeConverter.DefaultWholeThreshold),
            line.Flag("force-whole"),
            line.Optional("temp"));

        ConvertResult result = Convert(options, out IReadOnlyDictionary<string, long>? losses);
        Console.WriteLine(JsonSerializer.Serialize(result, ResultJson.Options));
        if (losses is not null)
        {
            Console.Error.WriteLine("lossCounts " + JsonSerializer.Serialize(losses));
        }

        return result.Outcome == "ok" ? 0 : 1;
    }

    public static ConvertResult Convert(ConvertOptions options, out IReadOnlyDictionary<string, long>? losses)
    {
        losses = null;
        if (options.TempFolder is not null)
        {
            Directory.CreateDirectory(options.TempFolder);
        }

        var result = new ConvertResult { Route = options.Route, Dataset = Path.GetFileName(options.DataFile), BlockSize = options.BlockSize, GcMode = GcMode() };
        using var sampler = new MemorySampler(options.TempFolder);
        var clock = Stopwatch.StartNew();
        try
        {
            using var reader = new EnvelopeReader(options.DataFile);
            string htmlFolder = Path.Combine(options.TemplateFolder ?? RepositoryPaths.HtmlTemplate(reader.DatasetId), "html");
            string markdownFolder = options.MarkdownTemplateFolder ?? RepositoryPaths.MarkdownTemplate(reader.DatasetId);
            string? outputFolder = Path.GetDirectoryName(Path.GetFullPath(options.OutputFile));
            if (outputFolder is not null)
            {
                Directory.CreateDirectory(outputFolder);
            }

            using (var stream = new FileStream(options.OutputFile, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
            using (var output = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" })
            {
                (long records, long groups, int blocks) = options.Route switch
                {
                    "c" => RunParts(reader, PartRenderer.Load(markdownFolder, "md", markdownFunctions: true), new MarkdownTemplateConverter(output)),
                    _ => RunFragments(reader, htmlFolder, options, output, result, out losses),
                };
                result.Records = records;
                result.Groups = groups;
                result.Blocks = blocks;
            }

            result.OutputBytes = new FileInfo(options.OutputFile).Length;
            result.Outcome = "ok";
        }
        catch (Exception ex) when (ex is OutOfMemoryException or IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or Scriban.Syntax.ScriptRuntimeException)
        {
            result.Outcome = "failed";
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        clock.Stop();
        sampler.Stop();
        result.ElapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds, 3);
        result.PeakManagedHeapBytes = sampler.PeakManagedHeapBytes;
        result.PeakWorkingSetBytes = sampler.PeakWorkingSetBytes;
        result.TempDiskBytes = sampler.TempDiskBytes;
        result.Gen0 = GC.CollectionCount(0);
        result.Gen1 = GC.CollectionCount(1);
        result.Gen2 = GC.CollectionCount(2);
        return result;
    }

    public static string GcMode() =>
        (GCSettings.IsServerGC ? "server" : "workstation") + (AppContext.TryGetSwitch("System.GC.Concurrent", out bool concurrent) && !concurrent ? "-nonconcurrent" : "-concurrent");

    private static (long, long, int) RunParts(EnvelopeReader reader, PartRenderer renderer, IPartSink sink)
    {
        (long records, long groups) = PartSequencer.Run(reader, renderer, sink);
        return (records, groups, 0);
    }

    private static (long, long, int) RunFragments(EnvelopeReader reader, string htmlFolder, ConvertOptions options, TextWriter output, ConvertResult result, out IReadOnlyDictionary<string, long>? losses)
    {
        losses = null;
        StreamingMarkdownConverter? streaming = null;
        IFragmentSink sink;
        switch (options.Route)
        {
            case "html":
                sink = new HtmlBaseline(output);
                break;
            case "a":
                sink = new FragmentStitchingConverter(output);
                break;
            case "b":
                sink = streaming = new StreamingMarkdownConverter(output);
                break;
            default:
                bool whole = HybridBySizeConverter.UseWhole(reader.RecordCount, options.WholeThreshold, options.ForceWhole);
                result.Branch = whole ? "whole" : "streaming";
                sink = whole ? new WholeDocumentConverter(output) : (streaming = new StreamingMarkdownConverter(output));
                break;
        }

        var assembler = new BlockAssembler(options.BlockSize, sink);
        (long records, long groups) = PartSequencer.Run(reader, PartRenderer.Load(htmlFolder, "html", markdownFunctions: false), assembler);
        losses = sink is FragmentStitchingConverter stitching
            ? new SortedDictionary<string, long>(StringComparer.Ordinal) { ["continuation fragments without a synthetic header"] = stitching.MissingSyntheticHeaders }
            : streaming?.Losses;
        return (records, groups, assembler.Blocks);
    }
}

internal sealed record ConvertOptions(
    string Route,
    string DataFile,
    string? TemplateFolder,
    string OutputFile,
    string? MarkdownTemplateFolder,
    int BlockSize,
    int WholeThreshold,
    bool ForceWhole,
    string? TempFolder);

/// <summary>The one JSON line printed by a conversion.</summary>
internal sealed class ConvertResult
{
    public string Route { get; set; } = string.Empty;

    public string Dataset { get; set; } = string.Empty;

    public long Records { get; set; }

    public long Groups { get; set; }

    public int Blocks { get; set; }

    public int BlockSize { get; set; }

    public string? Branch { get; set; }

    public long OutputBytes { get; set; }

    public double ElapsedSeconds { get; set; }

    public long PeakManagedHeapBytes { get; set; }

    public long PeakWorkingSetBytes { get; set; }

    public long TempDiskBytes { get; set; }

    public int Gen0 { get; set; }

    public int Gen1 { get; set; }

    public int Gen2 { get; set; }

    public string GcMode { get; set; } = string.Empty;

    public string Outcome { get; set; } = "failed";

    public string? Error { get; set; }
}

internal static class ResultJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
