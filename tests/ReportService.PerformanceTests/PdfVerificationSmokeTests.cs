using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using ReportService.Data;
using ReportService.Pdf.Verification;
using ReportService.Templates;
using ReportService.Testing;
using ReportService.Testing.Fixtures;

namespace ReportService.PerformanceTests;

/// <summary>
/// The verification's smoke on the reference environment: two documents are printed once
/// through the rendering host image — the same product composer, printer and verifier the
/// worker runs — and each is then read, aligned and checked in process twenty times after
/// three warm-ups. The class reports p50/p95/p99 of the whole verification of the large
/// document, of the per-page cost of the wide row, and of the source checks' share, together
/// with the run process's peak working set and managed heap and the rendering host
/// container's cgroup memory peak. It never runs in the hermetic or integration levels.
/// <para>
/// A measure above its threshold is reported as a <c>miss</c> in <c>performance.json</c> and
/// classified by a human; the class fails only when a document cannot be printed or does not
/// pass the verification, because then there is nothing to measure.
/// </para>
/// </summary>
public sealed class PdfVerificationSmokeTests
{
    /// <summary>The rendering host image the rendering level publishes.</summary>
    private const string HostImage = "report-service-rendering-host:0.2.0-dev";

    private const string LargeFixture = "B1000";

    private const string WideFixture = "X64K";

    private const string Package = "integrity-table";

    private const int WarmUps = 3;

    private const int Runs = 20;

    /// <summary>The fixture name of the 2,000-record stream the structural case builds.</summary>
    private const string StreamFixture = "S2000";

    /// <summary>The hard record maximum of a block, in records.</summary>
    private const int RecordMaximum = 2_000;

    /// <summary>The hard byte maximum of a block's record bytes.</summary>
    private const int RecordByteMaximum = 1_048_576;

    /// <summary>The records a printed page holds, at the density the measured documents show.</summary>
    private const int RecordsPerPage = 34;

    /// <summary>The pages the 2,000-record stream prints at that density.</summary>
    private const int StreamPageCount = 59;

    /// <summary>The writer of the stream's snapshot: the same compact form the fixture library writes.</summary>
    private static readonly JsonSerializerOptions StreamWriting = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    /// <summary>The measured verification runs on the worker's own thread: the process default.</summary>
    private const int MeasurementStackMiB = 1;

    /// <summary>The whole verification of the large document, at p95, in seconds.</summary>
    private const double LargeVerificationP95Seconds = 4.0;

    /// <summary>Verification time per page of the wide row, at p95, in seconds.</summary>
    private const double WidePerPageP95Seconds = 0.15;

    /// <summary>The source checks' share of the verification, at p95, as a fraction.</summary>
    private const double SourceShareP95 = 0.10;

    /// <summary>The rendering host container's memory peak, in MiB.</summary>
    private const double HostMemoryPeakMiB = 1_536;

    private static readonly string Root = Path.Combine(
        Path.GetTempPath(),
        "report-service-perf",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    [Fact]
    public async Task TheVerificationMeetsItsSmokeThresholdsOnTheReferenceEnvironment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Root);
        var fixtures = Path.Combine(Root, "fixtures");
        WriteFixtures(fixtures);
        var output = Path.Combine(Root, "out");
        Directory.CreateDirectory(output);
        var templates = Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "Templates");
        var package = Path.Combine(templates, Package);
        var manifest = Manifest();

        var large = await PrintAndMeasureAsync(LargeFixture, package, fixtures, templates, output, manifest, cancellationToken);
        var wide = await PrintAndMeasureAsync(WideFixture, package, fixtures, templates, output, manifest, cancellationToken);

        using var process = Process.GetCurrentProcess();
        var measures = new List<Threshold>
        {
            new("verification B1000 (whole)", "s", large.Whole, LargeVerificationP95Seconds),
            new("verification X64K per page", "s/page", wide.PerPage, WidePerPageP95Seconds),
            new("source checks share B1000", "fraction", large.SourceShare, SourceShareP95),
            new("source checks share X64K", "fraction", wide.SourceShare, SourceShareP95),
        };

        var report = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["environment"] = ReferenceEnvironment(),
            ["base_image_digest"] = BaseImageDigest(),
            ["machine"] = Environment.MachineName,
            ["warmups"] = WarmUps,
            ["runs"] = Runs,
            ["measurement_stack_mib"] = MeasurementStackMiB,
            ["documents"] = new[]
            {
                new { fixture = LargeFixture, pages = large.Pages, words = large.Words },
                new { fixture = WideFixture, pages = wide.Pages, words = wide.Words },
            },
            ["measures"] = measures.Select(measure => new
            {
                name = measure.Name,
                unit = measure.Unit,
                runs = measure.Samples.Count,
                p50 = Round(Percentile(measure.Samples, 0.50)),
                p95 = Round(Percentile(measure.Samples, 0.95)),
                p99 = Round(Percentile(measure.Samples, 0.99)),
                threshold = measure.Limit,
                verdict = Percentile(measure.Samples, 0.95) <= measure.Limit ? "pass" : "miss",
            }),
            ["process"] = new
            {
                peak_working_set_mib = Round(process.PeakWorkingSet64 / 1024d / 1024d),
                managed_heap_mib = Round(GC.GetTotalMemory(forceFullCollection: true) / 1024d / 1024d),
            },
            ["rendering_host_container"] = new
            {
                image = HostImage,
                memory_peak_mib = large.ContainerMemoryPeakMiB is { } peak ? Round(peak) : (double?)null,
                threshold_mib = HostMemoryPeakMiB,
                verdict = large.ContainerMemoryPeakMiB is { } measured
                    ? (measured <= HostMemoryPeakMiB ? "pass" : "miss")
                    : "not read",
            },
            ["pdf_worker_container_memory_peak"] = "carried, not measured",
        };

        report["verdict"] = measures.All(measure => Percentile(measure.Samples, 0.95) <= measure.Limit)
            && (large.ContainerMemoryPeakMiB is null || large.ContainerMemoryPeakMiB <= HostMemoryPeakMiB)
            ? "pass"
            : "miss";

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(RunEnvironment.EvidenceDirectory(), "performance.json");
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(json);

        // The instrument must have measured; a miss is the human's to classify, and it is in
        // the file with every number that produced it.
        Assert.Equal(Runs, large.Whole.Count);
        Assert.Equal(Runs, wide.Whole.Count);
    }

    /// <summary>
    /// The deepest unit stream a block may hold — 2,000 records, the hard record maximum — is
    /// judged on the worker's own thread: the alignment's depth follows the document's pages,
    /// never its records, so a stream twice the size of the largest printed document is aligned
    /// exactly as a small one is.
    /// </summary>
    [Fact]
    public async Task TheAlignerJudgesTwoThousandRecordsOnAnOrdinaryThread()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var datasets = Path.Combine(RepositoryPaths.Root, "samples", "datasets");
        var datasetPath = Path.Combine(datasets, "header-items-v1.grouped.10k.json");
        Assert.True(
            File.Exists(datasetPath),
            $"The pilot dataset {datasetPath} is missing. Generate it with: dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate");
        var dataset = FixtureDataset.Load(datasetPath, Path.Combine(datasets, FixtureGenerator.SumsFileName));

        // The stream: the pilot dataset's first records, in document order, unchanged except for
        // the header's own count. Its record bytes are the block's record bytes.
        var records = dataset.Items.Take(RecordMaximum).Select(item => (JsonObject)item.DeepClone()).ToList();
        var recordBytes = records.Sum(FixtureGenerator.CompactItemBytes);
        Assert.True(
            recordBytes <= RecordByteMaximum,
            $"The stream holds {recordBytes} record bytes, above the block's {RecordByteMaximum}.");

        var header = (JsonObject)dataset.Header.DeepClone();
        header["totals"]!["recordCount"] = records.Count;
        var snapshot = new JsonObject
        {
            ["contractVersion"] = FixtureGenerator.ContractVersion,
            ["header"] = header,
            ["items"] = new JsonArray([.. records.Select(record => (JsonNode)record)]),
        };

        var folder = Path.Combine(Root, "stream");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(
            Path.Combine(folder, StreamFixture + ".json"),
            snapshot.ToJsonString(StreamWriting),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        // The same composition the measured documents go through: the product's reader, render
        // context and composer, then the units the checks compare against.
        var templates = Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "Templates");
        var (_, text, _) = Compose(StreamFixture, Path.Combine(templates, Package), Manifest(), folder);
        var units = text.Units;
        var pages = StreamPages(units, RecordsPerPage);
        var evidence = new JsonObject
        {
            ["fixture"] = StreamFixture,
            ["records"] = records.Count,
            ["recordBytes"] = recordBytes,
            ["pages"] = pages.Count,
            ["units"] = units.Count,
        };
        await File.WriteAllTextAsync(
            Path.Combine(RunEnvironment.EvidenceDirectory(), "stream.json"),
            evidence.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"stream: records={records.Count} recordBytes={recordBytes} pages={pages.Count} units={units.Count}");

        ContentOrderAligner aligner = new();
        AlignmentOutcome outcome = null!;
        await Task.Run(() => outcome = aligner.Align(pages, units), cancellationToken);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"stream verdict: passed={outcome.Passed} reason={outcome.Reason ?? "none"}");
        Assert.True(outcome.Passed, $"The stream was refused with {outcome.Reason} on the worker's own thread.");
        Assert.Equal(units.Count, outcome.ConsumedCharacters.Count);
        for (var index = 0; index < units.Count; index++)
        {
            Assert.Equal(units[index].Normalised.Length, outcome.ConsumedCharacters[index]);
        }

        Assert.Equal(StreamPageCount, pages.Count);
    }

    // The pages a block of the stream prints: whole rows only, the document's own start on the
    // first page and its end on the last, at the record density the printed documents show.
    private static IReadOnlyList<string> StreamPages(IReadOnlyList<TextUnit> units, int recordsPerPage)
    {
        var pages = new List<string>();
        var page = new StringBuilder();
        var recordsOnPage = 0;
        var currentRecord = 0L;
        foreach (var unit in units)
        {
            if (unit.Kind == FragmentKind.Row && unit.RecordOrdinal != currentRecord)
            {
                if (recordsOnPage == recordsPerPage)
                {
                    pages.Add(page.ToString());
                    page.Clear();
                    recordsOnPage = 0;
                }

                currentRecord = unit.RecordOrdinal;
                recordsOnPage++;
            }

            page.Append(unit.Normalised);
        }

        if (page.Length > 0)
        {
            pages.Add(page.ToString());
        }

        return pages;
    }

    private static async Task<MeasuredDocument> PrintAndMeasureAsync(
        string fixture,
        string package,
        string fixtures,
        string templates,
        string output,
        RenderManifest manifest,
        CancellationToken cancellationToken)
    {
        var container = new ContainerBuilder(HostImage)
            .WithName($"report-service-perf-{fixture.ToLowerInvariant()}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]}")
            .WithCommand(
                "render",
                "--fixture", $"/fixtures/{fixture}.json",
                "--template", $"/packages/{Package}",
                "--out", "/out")
            .WithBindMount(fixtures, "/fixtures", AccessMode.ReadWrite)
            .WithBindMount(templates, "/packages", AccessMode.ReadWrite)
            .WithBindMount(output, "/out", AccessMode.ReadWrite)
            .Build();

        await using (container.ConfigureAwait(false))
        {
            await container.StartAsync(cancellationToken).ConfigureAwait(false);
            var peak = await WatchMemoryAsync(container, cancellationToken);
            var exit = await container.GetExitCodeAsync(cancellationToken);
            Assert.True(exit == 0, $"the rendering host exited {exit} for {fixture}; its manifest is in {output}.");

            var result = Path.Combine(output, fixture + ".json");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result, cancellationToken).ConfigureAwait(false));
            var pdf = Path.Combine(output, document.RootElement.GetProperty("pdf").GetString()!);
            Assert.True(
                document.RootElement.TryGetProperty("verificationPassed", out var passed) && passed.GetBoolean(),
                $"{fixture} was printed and did not pass the product verification; there is nothing to measure.");

            var (composed, text, source) = Compose(fixture, package, manifest, fixtures);
            var verifier = new BlockVerifier();
            var margins = PlacementConvention.FromMillimetres(manifest.Pdf.MarginMm);

            // The verification runs on the thread the worker's activity runs on: an ordinary
            // thread of the pool, with the process's own stack. A block of this size that
            // cannot be aligned there would crash a job, so the measurement must not hide it
            // behind a thread of its own.
            var whole = new List<double>(Runs);
            var perPage = new List<double>(Runs);
            var share = new List<double>(Runs);
            VerificationResult? last = null;
            Exception? failure = null;
            await Task.Run(
                () =>
                {
                    try
                    {
                        for (var warmUp = 0; warmUp < WarmUps; warmUp++)
                        {
                            Measure(verifier, pdf, margins, composed, text, source, out _, out _);
                        }

                        for (var run = 0; run < Runs; run++)
                        {
                            Measure(verifier, pdf, margins, composed, text, source, out var sourceMs, out var readAndAlignMs, out var verification);
                            last = verification;
                            Assert.True(
                                verification.Passed,
                                $"{fixture} read {verification.Reason} on run {run}; a refused document cannot be measured.");
                            var totalSeconds = (sourceMs + readAndAlignMs) / 1000d;
                            whole.Add(totalSeconds);
                            perPage.Add(totalSeconds / Math.Max(verification.Pages, 1));
                            share.Add(sourceMs / (double)Math.Max(sourceMs + readAndAlignMs, 1));
                        }
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                }).ConfigureAwait(false);
            if (failure is not null)
            {
                throw new InvalidOperationException($"the measurement of {fixture} did not finish: {failure.Message}", failure);
            }

            return new MeasuredDocument(
                whole,
                perPage,
                share,
                last!.Pages,
                last.Words,
                peak);
        }
    }

    // One measured verification: the source checks first, timed, then the read, the alignment
    // and the page checks, whose own two timings the result reports.
    private static void Measure(
        BlockVerifier verifier,
        string pdf,
        PageMargins margins,
        ComposedBlock composed,
        ExtractedText text,
        SourceCheckInput source,
        out long sourceMs,
        out long readAndAlignMs,
        out VerificationResult verification)
    {
        var checks = verifier.CheckSource(source, out sourceMs);
        Assert.True(checks.Passed, $"the source checks refused the composed block with {checks.Reason}");
        verification = verifier.Verify(new PrintedCheckInput(pdf, 0, margins, composed, text, sourceMs));
        readAndAlignMs = verification.ReadAndAlignMs;
    }

    private static void Measure(
        BlockVerifier verifier,
        string pdf,
        PageMargins margins,
        ComposedBlock composed,
        ExtractedText text,
        SourceCheckInput source,
        out long sourceMs,
        out long readAndAlignMs) =>
        Measure(verifier, pdf, margins, composed, text, source, out sourceMs, out readAndAlignMs, out _);

    // The composition of the fixture with the package, in process, exactly as the worker does
    // it: the block read with the data contract's reader, its values mapped to the render
    // context, and the package's prepared parts composed in block mode.
    private static (ComposedBlock Composed, ExtractedText Text, SourceCheckInput Source) Compose(
        string fixture,
        string package,
        RenderManifest manifest,
        string fixtures)
    {
        var snapshot = File.ReadAllBytes(Path.Combine(fixtures, fixture + ".json"));
        using var stream = new MemoryStream(snapshot, writable: false);
        IReadOnlyDictionary<string, object?> header = new Dictionary<string, object?>(StringComparer.Ordinal);
        var records = new List<ComposedRecord>();
        foreach (var value in HeaderItemsReader.ReadAsync(stream, CancellationToken.None).ToBlockingEnumerable())
        {
            if (value.Ordinal == 0)
            {
                header = RenderContextFactory.Header(value.Json.Span);
            }
            else
            {
                records.Add(new ComposedRecord(value.Ordinal, RenderContextFactory.Item(value)));
            }
        }

        var files = new FolderTemplatePackageFiles(package);
        var template = new ScribanTemplateAdapter().Prepare(manifest, files);
        var composer = new BlockComposer(new ScribanTemplateAdapter(), TimeProvider.System);
        var composition = composer.Compose(
            new CompositionRequest(
                template,
                new CompositionBlock(0, records.Count, 1, IsFirst: true, IsLast: true),
                header,
                records,
                new CompositionRender("es-AR", "America/Argentina/Buenos_Aires", DateTimeOffset.UnixEpoch),
                manifest.GroupKey),
            CancellationToken.None);
        var text = new TextUnitExtractor().Extract(composition);
        var block = new BlockFacts(
            0,
            1,
            records.Select(record => new BlockRecord(record.Ordinal, record.Values)).ToList(),
            header);
        var source = new SourceCheckInput(
            new PartitionFacts(1, records.Count),
            new IndexFacts(0, records.Count, 1),
            block,
            composition,
            text,
            manifest.Pdf.Verification,
            IsFirstBlock: true);
        return (composition, text, source);
    }

    private static RenderManifest Manifest()
    {
        var manifest = new ReportService.TemplatePackages.Manifest.TemplateManifest
        {
            ManifestVersion = "1.0",
            Id = Package,
            Version = "1.0.0",
            Engine = ReportService.TemplatePackages.Manifest.TemplateEngine.Scriban,
            DeclaredEngineVersion = "7.5.1",
            Outputs = new ReportService.TemplatePackages.Manifest.TemplateOutputs
            {
                Html = new ReportService.TemplatePackages.Manifest.TemplateHtmlOutput
                {
                    Parts = new ReportService.TemplatePackages.Manifest.TemplateParts
                    {
                        DocumentStart = "html/document-start.html.sbn",
                        Row = "html/row.html.sbn",
                        DocumentEnd = "html/document-end.html.sbn",
                    },
                    Styles = ["html/styles.css"],
                },
                Pdf = new ReportService.TemplatePackages.Manifest.TemplatePdfOutput
                {
                    Paper = "A4",
                    Orientation = "portrait",
                    MarginMm = new ReportService.TemplatePackages.Manifest.TemplateMargins { Top = 15, Right = 12, Bottom = 18, Left = 12 },
                    PageTotals = new ReportService.TemplatePackages.Manifest.TemplatePageTotals { Enabled = false },
                    Verification = new ReportService.TemplatePackages.Manifest.TemplatePdfVerification
                    {
                        Anchors = new ReportService.TemplatePackages.Manifest.TemplatePdfAnchors
                        {
                            Header = ["$.reportTitle", "$.customer.name", "$.currency", "$.totals.recordCount"],
                            Item = ["$.sequence", "$.date", "$.description", "$.amount"],
                        },
                    },
                },
            },
        };
        return RenderManifestMapping.From(
            manifest,
            ReportService.TemplatePackages.Manifest.TemplateEngine.Scriban,
            "7.5.1");
    }

    private void WriteFixtures(string folder)
    {
        var datasets = Path.Combine(RepositoryPaths.Root, "samples", "datasets");
        var dataset = Path.Combine(datasets, "header-items-v1.grouped.10k.json");
        Assert.True(
            File.Exists(dataset),
            $"The pilot dataset {dataset} is missing. Generate it with: dotnet run --project samples/datasets/ReportService.Samples.DatasetGenerator -c Release -- generate");
        var generator = new FixtureGenerator(FixtureDataset.Load(dataset, Path.Combine(datasets, FixtureGenerator.SumsFileName)));
        Directory.CreateDirectory(folder);
        generator.WriteFolder(folder);
    }

    // The rendering host container's cgroup peak, read while it runs: the container is gone
    // once it exits, so the value is polled and the largest reading kept.
    private static async Task<double?> WatchMemoryAsync(IContainer container, CancellationToken cancellationToken)
    {
        double? peak = null;
        try
        {
            while (container.State == TestcontainersStates.Running && !cancellationToken.IsCancellationRequested)
            {
                var exec = await container.ExecAsync(["cat", "/sys/fs/cgroup/memory.peak"], cancellationToken).ConfigureAwait(false);
                if (exec.ExitCode == 0
                    && long.TryParse(exec.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
                {
                    var mib = bytes / 1024d / 1024d;
                    peak = peak is null ? mib : Math.Max(peak.Value, mib);
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A peak that could not be read is recorded as absent, never invented.
        }

        return peak;
    }

    private static string ReferenceEnvironment()
    {
        var page = Path.Combine(RepositoryPaths.Root, "docs", "reference-environment.md");
        if (!File.Exists(page))
        {
            return "unknown (docs/reference-environment.md not found)";
        }

        var match = Regex.Match(File.ReadAllText(page), @"(?i)page\s+revision\W{0,4}(?<revision>\d+)", RegexOptions.CultureInvariant);
        return match.Success ? $"docs/reference-environment.md page revision {match.Groups["revision"].Value}" : "unknown";
    }

    private static string BaseImageDigest()
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "image", "inspect", "localhost:5055/report-service-pdf-base:1.63.0-10.0.13-noble",
                     "--format", "{{index .RepoDigests 0}}",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return "unknown";
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && output.Length > 0 ? output : "unknown";
    }

    private static double Percentile(IReadOnlyList<double> samples, double percentile)
    {
        var ordered = samples.OrderBy(value => value).ToList();
        var rank = (int)Math.Ceiling(percentile * ordered.Count) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Count - 1)];
    }

    private static double Round(double value) => Math.Round(value, 6);

    /// <summary>One measure and the samples it was taken over.</summary>
    private sealed record Threshold(string Name, string Unit, IReadOnlyList<double> Samples, double Limit);

    /// <summary>What one printed document measured.</summary>
    private sealed record MeasuredDocument(
        IReadOnlyList<double> Whole,
        IReadOnlyList<double> PerPage,
        IReadOnlyList<double> SourceShare,
        int Pages,
        int Words,
        double? ContainerMemoryPeakMiB);
}
