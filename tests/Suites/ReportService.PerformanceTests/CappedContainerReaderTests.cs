using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ReportService.DataReaderProbe;
using ReportService.Testing;

namespace ReportService.PerformanceTests;

/// <summary>
/// The data pass inside a Linux container capped at 128 MiB of memory, swap equal to memory and
/// no network: a one-million-record gzip snapshot larger than the cap completes, and every
/// hostile input at the two-mebibyte bounds - the validated two-mebibyte image record included -
/// ends with its expected outcome with the process's peak resident memory at most 64 MiB. The
/// probe is published for Linux from its project and run in the runtime image of the version
/// the tests run on. Needs a container runtime; without one the class fails by name.
/// </summary>
[Collection(nameof(MemoryMeasured))]
[Trait("Requires", "docker")]
public sealed class CappedContainerReaderTests : IDisposable
{
    private const long PeakAllowance = 64L * 1024 * 1024;
    private const int VolumeRecords = 1_000_000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "capped-reader", Guid.NewGuid().ToString("N"));

    public CappedContainerReaderTests() => Directory.CreateDirectory(_folder);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnderA128MebibyteCapTheVolumeCompletesAndEveryHostileInputEndsAsExpected()
    {
        var probe = await PublishProbeAsync();
        var data = Path.Combine(_folder, "data");
        Directory.CreateDirectory(data);
        WriteVolume(Path.Combine(data, "volume-1m.json.gz"));
        foreach (var hostile in HostileCases.All)
        {
            hostile.Write(Path.Combine(data, hostile.FileName));
        }

        var figures = new List<string>();
        var volume = await RunInContainerAsync(probe, data, "volume-1m.json.gz", withSchema: false);
        figures.Add(Figure("volume-1m", volume));
        Assert.Equal("ok", volume.GetProperty("outcome").GetString());
        Assert.Equal(VolumeRecords, volume.GetProperty("records").GetInt64());

        foreach (var hostile in HostileCases.All)
        {
            var result = await RunInContainerAsync(probe, data, hostile.FileName, hostile.WithSchema);
            figures.Add(Figure(hostile.Name, result));
            Assert.Equal(hostile.ExpectedCode, result.GetProperty("code").GetString());
            if (hostile.ExpectedLimitOrReason is { } token)
            {
                var actual = result.GetProperty("limit").GetString() is { } limit ? "limit=" + limit : "reason=" + result.GetProperty("reason").GetString();
                Assert.Equal(token, actual);
            }

            Assert.True(result.GetProperty("peakRssBytes").GetInt64() <= PeakAllowance, Figure(hostile.Name, result));
        }

        var evidence = string.Join(Environment.NewLine, figures) + Environment.NewLine;
        RunEnvironment.WriteEvidence("capped-container-reader.evidence.txt", Encoding.UTF8.GetBytes(evidence));
        TestContext.Current.TestOutputHelper?.WriteLine(evidence);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is not a test failure.
        }
    }

    private static string Figure(string name, JsonElement result) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: outcome={result.GetProperty("outcome").GetString()} code={result.GetProperty("code")} limit={result.GetProperty("limit")} reason={result.GetProperty("reason")} records={result.GetProperty("records")} peakRss={result.GetProperty("peakRssBytes").GetInt64() / 1048576.0:F1} MiB seconds={result.GetProperty("seconds")}");

    private async Task<string> PublishProbeAsync()
    {
        var output = Path.Combine(_folder, "probe");
        var project = Path.Combine(RepositoryPaths.Root, "tests", "Suites", "ReportService.DataReaderProbe", "ReportService.DataReaderProbe.csproj");
        var (exit, text) = await RunAsync("dotnet", ["publish", project, "-c", "Release", "-r", "linux-x64", "--self-contained", "false", "-o", output]);
        Assert.True(exit == 0, "the probe was not published: " + text);
        return output;
    }

    private static async Task<JsonElement> RunInContainerAsync(string probe, string data, string file, bool withSchema)
    {
        var image = "mcr.microsoft.com/dotnet/runtime:" + Environment.Version.ToString(2);
        List<string> arguments =
        [
            "run", "--rm", "--network", "none", "--memory", "128m", "--memory-swap", "128m",
            "-v", probe + ":/probe:ro", "-v", data + ":/data:ro",
            image, "dotnet", "/probe/ReportService.DataReaderProbe.dll", "/data/" + file,
        ];
        if (withSchema)
        {
            arguments.Add("--schema");
        }

        var (exit, text) = await RunAsync("docker", arguments);
        Assert.True(exit == 0, file + ": the probe did not complete under the cap (exit " + exit + "): " + text);
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last(entry => entry.StartsWith('{'));
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    private static async Task<(int Exit, string Output)> RunAsync(string fileName, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException(fileName + " did not start");
        var output = process.StandardOutput.ReadToEndAsync(Cancellation);
        var error = process.StandardError.ReadToEndAsync(Cancellation);
        await process.WaitForExitAsync(Cancellation);
        return (process.ExitCode, await output + await error);
    }

    // One million ordinary records, gzip: larger uncompressed than the cap the pass runs under.
    private static void WriteVolume(string path)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        writer.Write("""{"contractVersion":"1.0","header":{"title":"Volume","currency":"EUR"},"items":[""");
        for (var sequence = 1; sequence <= VolumeRecords; sequence++)
        {
            if (sequence > 1)
            {
                writer.Write(',');
            }

            writer.Write(string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"sequence":{{sequence}},"date":"2026-11-{{sequence % 28 + 1:D2}}","description":"Payment of the monthly statement number {{sequence}} to the supplier account","quantity":{{sequence % 9 + 1}},"unitPrice":{{sequence % 997 / 10m}},"amount":{{sequence % 9973 / 100m}}}"""));
        }

        writer.Write("]}");
    }
}
