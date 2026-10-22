using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using PdfSharp.Pdf;

namespace PdfAssemblySpike;

/// <summary>
/// Command-line entry point of the PDF assembly experiment. Exit codes: 0 when
/// the command succeeded and every check passed, 1 when a check failed or a run
/// errored, 2 for invalid arguments. Memory, time and disk are measurements and
/// never decide the exit code.
/// </summary>
internal static class Program
{
    private const string Usage = """
        versions                                                    runtime, tool and library versions
        probe     --out DIR [--font-dir DIR]                        pre-flight checks of library behaviour
        generate  --pages 1000|5000|10000 [--seed N] --out DIR      partial PDFs + manifest.json
        assemble  --variant V --manifest FILE --out FILE --temp DIR --metrics FILE [--batch 25]
        qpdf-args --manifest FILE [--parts-dir DIR] --output FILE --args FILE
        verify    --manifest FILE --pdf FILE --numbered true|false --pages all|sample --temp DIR --report FILE
        controls  --manifest FILE --temp DIR --report FILE                positive and negative controls of the structural check
        repro     --manifest FILE --repeat-manifest FILE --temp DIR --report FILE   masked comparison of two generations + its controls
        matrix    --out DIR [--work DIR] [--repetitions 3] [--sizes 1000,5000,10000] [--quick]
        """;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        Dictionary<string, string> options;
        try
        {
            options = ParseOptions(args.AsSpan(1));
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string? Get(string name) => options.TryGetValue(name, out string? value) ? value : null;
        string Need(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required.");

        try
        {
            switch (args[0])
            {
                case "versions":
                    {
                        var versions = Versions();
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(versions, SetManifest.Json));
                        bool ok = !OperatingSystem.IsLinux() || (versions["fontsPresent"] is true && versions["timeLabelsPresent"] is true);
                        return ok ? 0 : 1;
                    }

                case "probe":
                    return Probe.Run(Need("out"), Get("font-dir"));
                case "generate":
                    {
                        DejaVuFontResolver.Install(Get("font-dir"));
                        int pages = int.Parse(Need("pages"), CultureInfo.InvariantCulture);
                        ulong seed = Get("seed") is { } s ? ulong.Parse(s, CultureInfo.InvariantCulture) : PartGenerator.DefaultSeed;
                        string manifest = PartGenerator.Generate(pages, seed, Need("out"));
                        Console.WriteLine($"generated {manifest}");
                        return 0;
                    }

                case "assemble":
                    DejaVuFontResolver.Install(Get("font-dir"));
                    return Assembler.Run(Need("variant"), Need("manifest"), Need("out"), Need("temp"), Need("metrics"),
                        int.Parse(Get("batch") ?? "25", CultureInfo.InvariantCulture));
                case "qpdf-args":
                    {
                        string manifest = Need("manifest");
                        Assembler.WriteQpdfArguments(SetManifest.Load(manifest), manifest, Get("parts-dir"), Need("output"), Need("args"));
                        return 0;
                    }

                case "verify":
                    {
                        string mode = Need("pages");
                        if (mode is not ("all" or "sample"))
                        {
                            throw new ArgumentException("--pages must be all or sample.");
                        }

                        string report = Need("report");
                        var result = Verifier.Verify(Need("manifest"), Need("pdf"), bool.Parse(Need("numbered")), mode, Need("temp"),
                            Path.GetDirectoryName(Path.GetFullPath(report))!);
                        ResultsWriter.WriteJson(report, result);
                        Console.WriteLine($"structure {(result.StructurePass ? "PASS" : "FAIL")}, order {(result.OrderPass ? "PASS" : "FAIL")}, numbering {(result.NumberingPass ? "PASS" : "FAIL")}, pixels {(result.PixelPass ? "PASS" : "FAIL")}{(result.Pass ? string.Empty : " — first failure: " + result.FirstFailure)}");
                        return result.Pass ? 0 : 1;
                    }

                case "controls":
                    {
                        var (pass, controls) = Verifier.RunControls(Need("manifest"), Need("temp"));
                        ResultsWriter.WriteJson(Need("report"), MatrixRunner.ControlsReport(pass, controls));
                        foreach (var c in controls)
                        {
                            Console.WriteLine($"{c.Id}: expected {c.Expected}, got {c.Actual} (exit {c.ExitCode}, {c.WarningLines} warnings, {c.ExemptWarningLines} exempt{(c.FailReason.Length > 0 ? ", " + c.FailReason : string.Empty)}) {(c.AsExpected ? "PASS" : "FAIL " + c.Note)}");
                        }

                        Console.WriteLine($"controls: {(pass ? "PASS" : "FAIL")}");
                        return pass ? 0 : 1;
                    }

                case "repro":
                    {
                        var repro = MaskedComparison.CompareSets(Need("manifest"), Need("repeat-manifest"), Need("temp"));
                        ResultsWriter.WriteJson(Need("report"), MaskedComparison.Report(repro));
                        MatrixRunner.PrintRepro(repro);
                        Console.WriteLine($"repro: {(repro.Pass ? "PASS" : "FAIL")}");
                        return repro.Pass ? 0 : 1;
                    }

                case "matrix":
                    {
                        int[] sizes = (Get("sizes") ?? "1000,5000,10000").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                        return MatrixRunner.Run(Need("out"), Get("work") ?? "/work", int.Parse(Get("repetitions") ?? "3", CultureInfo.InvariantCulture),
                            sizes, options.ContainsKey("quick"), Get("font-dir"), int.Parse(Get("batch") ?? "25", CultureInfo.InvariantCulture));
                    }

                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (InvalidDataException e)
        {
            // A generated file without its page count or without the expected random-field shape.
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>Versions of the runtime, the PDF library and the external tools, plus the container limits.</summary>
    public static Dictionary<string, object?> Versions()
    {
        var pdfLibrary = typeof(PdfDocument).Assembly;
        var versions = new Dictionary<string, object?>
        {
            ["runtimeVersion"] = Environment.Version.ToString(),
            ["frameworkDescription"] = RuntimeInformation.FrameworkDescription,
            ["osDescription"] = RuntimeInformation.OSDescription,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processorCount"] = Environment.ProcessorCount,
            ["serverGc"] = GCSettings.IsServerGC,
            ["pdfLibraryAssemblyVersion"] = pdfLibrary.GetName().Version?.ToString(),
            ["pdfLibraryInformationalVersion"] = pdfLibrary.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["qpdf"] = FirstLine("qpdf", "--version"),
            ["qpdfReleaseVersion"] = Environment.GetEnvironmentVariable("QPDF_VERSION"),
            ["qpdfArchiveSha256"] = Environment.GetEnvironmentVariable("QPDF_ARCHIVE_SHA256"),
            ["pdftotext"] = FirstLine("pdftotext", "-v"),
            ["pdftoppm"] = FirstLine("pdftoppm", "-v"),
            ["gnuTime"] = FirstLine(MatrixRunner.TimeProgram, "--version"),
            ["packageList"] = AllLines("dpkg-query", "-W", "poppler-utils", "fonts-dejavu-core", "time"),
            ["memoryMax"] = ReadFile("/sys/fs/cgroup/memory.max"),
            ["memorySwapMax"] = ReadFile("/sys/fs/cgroup/memory.swap.max"),
            ["cpuMax"] = ReadFile("/sys/fs/cgroup/cpu.max"),
            ["userId"] = FirstLine("id", "-u"),
        };
        string dir = DejaVuFontResolver.DefaultDirectory;
        versions["fontsPresent"] = File.Exists(Path.Combine(dir, "DejaVuSans.ttf")) && File.Exists(Path.Combine(dir, "DejaVuSans-Bold.ttf"));
        string timeCheck = Path.Combine(Path.GetTempPath(), "time-label-check.txt");
        try
        {
            ExternalTool.Run(MatrixRunner.TimeProgram, ["-v", "-o", timeCheck, "true"]);
            string labels = File.ReadAllText(timeCheck);
            versions["timeLabelsPresent"] = labels.Contains(TimeOutput.MaxRssLabel, StringComparison.Ordinal)
                && labels.Contains(TimeOutput.WallLabel, StringComparison.Ordinal);
            File.Delete(timeCheck);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            versions["timeLabelsPresent"] = false;
        }

        return versions;
    }

    private static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            }

            string name = args[i][2..];
            if (name == "quick")
            {
                options[name] = "true";
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"--{name} needs a value.");
            }

            options[name] = args[++i];
        }

        return options;
    }

    private static string? FirstLine(string program, params string[] arguments) =>
        AllLines(program, arguments)?.FirstOrDefault();

    private static List<string>? AllLines(string program, params string[] arguments)
    {
        try
        {
            var result = ExternalTool.Run(program, arguments);
            return [.. (result.StandardOutput + result.StandardError).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}
