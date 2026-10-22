using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using StreamingDataSpike.Measuring;
using StreamingDataSpike.Parsing;
using StreamingDataSpike.Rendering;

namespace StreamingDataSpike;

/// <summary>The measured commands: <c>prepare</c>, <c>parse</c>, <c>render</c>, <c>compare</c>.</summary>
internal static class Commands
{
    /// <summary>Gzips every <c>header-items-v1.*.json</c> of a folder (optimal compression).</summary>
    public static async Task<int> PrepareAsync(Arguments arguments)
    {
        var folder = arguments.Required("--datasets");
        arguments.EnsureAllUsed(0);
        foreach (var path in Directory.GetFiles(folder, "header-items-v1.*.json").Order(StringComparer.Ordinal))
        {
            var target = path + ".gz";
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.SequentialScan))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 65_536))
            await using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                await source.CopyToAsync(gzip).ConfigureAwait(false);
            }

            using var line = new ResultLine("prepare", path);
            line.Add("bytes", new FileInfo(path).Length)
                .Add("sha256", await Sha256Async(path).ConfigureAwait(false))
                .Add("gzipFile", Path.GetFileName(target))
                .Add("gzipBytes", new FileInfo(target).Length)
                .Add("gzipSha256", await Sha256Async(target).ConfigureAwait(false));
            Console.Out.WriteLine(line.ToString());
        }

        return 0;
    }

    /// <summary>Reads, validates and counts blocks; one JSON result line.</summary>
    public static async Task<int> ParseAsync(Arguments arguments)
    {
        var file = arguments.Positional(0, "FILE");
        var limits = arguments.Limits();
        arguments.EnsureAllUsed(1);

        var meter = new Meter();
        var run = new ParseRun();
        EnvelopeException? error = null;
        try
        {
            await run.ExecuteAsync(file, limits, meter, CancellationToken.None).ConfigureAwait(false);
        }
        catch (EnvelopeException exception)
        {
            error = exception;
        }

        var reading = meter.Finish();
        using var line = new ResultLine("parse", file);
        line.Add("status", error is null ? "ok" : "error")
            .Add("records", run.Reader?.RecordsRead ?? 0)
            .Add("groups", run.Validator?.GroupRuns ?? 0)
            .Add("blocks", run.Validator?.Blocks ?? 0)
            .Add("amount", run.Validator?.Amount ?? 0m)
            .Add("headerBytes", run.Header?.ByteLength ?? 0)
            .Add("maxRecordBytes", run.Reader?.MaxRecordBytes ?? 0)
            .Add("maxBufferBytes", (long)(run.Reader?.MaxBufferBytes ?? 0))
            .Add("inputBytes", run.Reader?.InputBytes ?? 0)
            .Add("initialBuffer", (long)limits.InitialBuffer)
            .AddReading(reading, run.Reader?.RecordsRead ?? 0);
        if (error is not null)
        {
            line.AddError(error);
        }

        Console.Out.WriteLine(line.ToString());
        if (error is not null)
        {
            Console.Error.WriteLine(error.ToErrorLine());
            return 3;
        }

        return 0;
    }

    /// <summary>Renders a document through one engine into a hashing sink.</summary>
    public static async Task<int> RenderAsync(Arguments arguments)
    {
        var file = arguments.Positional(0, "FILE");
        var templateId = arguments.Required("--template");
        var engine = arguments.Required("--engine");
        var context = arguments.Option("--context") ?? "per-record";
        var limits = arguments.Limits();
        arguments.EnsureAllUsed(1);
        if (!Renderers.Engines.Contains(engine) || context is not ("per-record" or "per-block"))
        {
            throw new UsageException("Unknown engine or context mode.");
        }

        var meter = new Meter();
        var renderer = Renderers.Prepared(engine, templateId, out var prepareMilliseconds, out var parts);
        var document = new DocumentRenderer([renderer], parts.HasGroups, context == "per-block");
        var outcome = await document.RenderAsync(file, limits, meter, CancellationToken.None).ConfigureAwait(false);
        var reading = meter.Finish();
        var result = outcome.Engines[0];
        using var line = new ResultLine("render", file);
        line.Add("template", templateId)
            .Add("engine", engine)
            .Add("context", context)
            .Add("status", "ok")
            .Add("records", outcome.Records)
            .Add("blocks", outcome.Blocks)
            .Add("sha256", result.Sha256)
            .Add("htmlBytes", result.Bytes)
            .Add("templateParseMilliseconds", prepareMilliseconds)
            .Add("renderSeconds", result.RenderSeconds)
            .Add("renderMicrosecondsPerRecord", outcome.Records > 0 ? result.RenderSeconds * 1_000_000 / outcome.Records : 0)
            .Add("maxBufferBytes", (long)outcome.MaxBufferBytes)
            .AddReading(reading, outcome.Records);
        Console.Out.WriteLine(line.ToString());
        return 0;
    }

    /// <summary>
    /// Renders a document through the three engines, compares every part of
    /// every record and reports the three document hashes. With
    /// <c>--parallel N</c> the document is rendered N times at once with the
    /// same prepared templates (one context per render).
    /// </summary>
    public static async Task<int> CompareAsync(Arguments arguments)
    {
        var file = arguments.Positional(0, "FILE");
        var templateId = arguments.Required("--template");
        var culture = arguments.Option("--culture");
        var parallel = (int)arguments.Number("--parallel", 1, 1);
        var limits = arguments.Limits();
        arguments.EnsureAllUsed(1);

        var result = await CompareCore(file, templateId, culture, parallel, limits, null).ConfigureAwait(false);
        Console.Out.WriteLine(result.Line);
        return result.Identical ? 0 : 1;
    }

    /// <summary>The compare logic, shared with the self-check.</summary>
    public static async Task<CompareResult> CompareCore(string file, string templateId, string? culture, int parallel, EnvelopeLimits limits, Action<EnvelopeRecord, string>? rowObserver)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        if (culture is not null)
        {
            var info = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = info;
            CultureInfo.CurrentUICulture = info;
            CultureInfo.DefaultThreadCurrentCulture = info;
            CultureInfo.DefaultThreadCurrentUICulture = info;
        }

        try
        {
            var meter = new Meter();
            var renderers = new List<IRecordRenderer>();
            var prepare = new Dictionary<string, double>(StringComparer.Ordinal);
            var hasGroups = false;
            foreach (var engine in Renderers.Engines)
            {
                renderers.Add(Renderers.Prepared(engine, templateId, out var milliseconds, out var parts));
                prepare[engine] = milliseconds;
                hasGroups = parts.HasGroups;
            }

            var document = new DocumentRenderer(renderers, hasGroups, perBlock: false) { RowObserver = rowObserver };
            var runs = await Task.WhenAll(Enumerable.Range(0, parallel).Select(index =>
                Task.Run(() => document.RenderAsync(file, limits, index == 0 ? meter : null, CancellationToken.None)))).ConfigureAwait(false);
            var reading = meter.Finish();

            var first = runs[0];
            var mismatches = runs.Sum(run => run.Mismatches);
            var hashes = first.Engines.ToDictionary(engine => engine.Engine, engine => engine.Sha256, StringComparer.Ordinal);
            var enginesEqual = hashes.Values.Distinct(StringComparer.Ordinal).Count() == 1;
            var parallelEqual = runs.All(run => run.Engines.Select(engine => engine.Sha256).SequenceEqual(first.Engines.Select(engine => engine.Sha256)));

            using var line = new ResultLine("compare", file);
            line.Add("template", templateId)
                .Add("culture", culture ?? "invariant")
                .Add("parallel", (long)parallel)
                .Add("records", first.Records)
                .Add("mismatches", mismatches)
                .Add("firstMismatch", runs.Select(run => run.FirstMismatch).FirstOrDefault(text => text is not null))
                .Add("sha256Scriban", hashes["scriban"])
                .Add("sha256Handlebars", hashes["handlebars"])
                .Add("sha256Liquid", hashes["liquid"])
                .Add("htmlBytes", first.Engines[0].Bytes)
                .Add("enginesIdentical", enginesEqual)
                .Add("parallelIdentical", parallelEqual)
                .Add("prepareMillisecondsScriban", prepare["scriban"])
                .Add("prepareMillisecondsHandlebars", prepare["handlebars"])
                .Add("prepareMillisecondsLiquid", prepare["liquid"])
                .AddReading(reading, first.Records);
            return new CompareResult(line.ToString(), mismatches == 0 && enginesEqual && parallelEqual, hashes["scriban"], first.Records);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = null;
            CultureInfo.DefaultThreadCurrentUICulture = null;
        }
    }

    /// <summary>SHA-256 of a file, read sequentially.</summary>
    public static async Task<string> Sha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }
}

/// <summary>Result of a compare run.</summary>
internal sealed record CompareResult(string Line, bool Identical, string Sha256, long Records);

/// <summary>One parse of a file: the reader, the header and the validator stay readable after an error.</summary>
internal sealed class ParseRun
{
    public EnvelopeReader? Reader { get; private set; }

    public EnvelopeHeader? Header { get; private set; }

    public EnvelopeValidator? Validator { get; private set; }

    public async Task ExecuteAsync(string file, EnvelopeLimits limits, Meter? meter, CancellationToken cancellationToken)
    {
        await using var input = InputFiles.Open(file);
        Reader = new EnvelopeReader(input, limits);
        Header = await Reader.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        Validator = new EnvelopeValidator(Header, limits);
        await foreach (var record in Reader.ReadItemsAsync(cancellationToken).ConfigureAwait(false))
        {
            Validator.Observe(record);
            meter?.OnRecord();
        }

        Validator.Complete(Reader.InputBytes);
    }
}
