using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReportService.Pdf.Verification;

namespace ReportService.Eng.ReaderAgreement;

/// <summary>
/// The reader agreement re-run. <c>reading</c> reads every document of the agreed
/// set with the product's reader and writes the reading in the format the measured
/// experiment reads, three times, requiring the three to be identical. <c>compare</c>
/// compares a reading with another one word for word and page for page.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitDifference = 1;
    private const int ExitUsage = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return Usage();
            }

            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index += 2)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                {
                    return Usage();
                }

                options[args[index][2..]] = args[index + 1];
            }

            return args[0] switch
            {
                "reading" when options.ContainsKey("set") && options.ContainsKey("out") => Reading(options["set"], options["out"]),
                "compare" when options.ContainsKey("measured") && options.ContainsKey("product") => Compare(options["measured"], options["product"]),
                _ => Usage(),
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return ExitDifference;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: reading --set <pdf-set.json> --out <dir> | compare --measured <dir> --product <dir>");
        return ExitUsage;
    }

    /// <summary>Reads every document of the set three times and writes the readings.</summary>
    private static int Reading(string setPath, string outDirectory)
    {
        var set = JsonSerializer.Deserialize<PdfSet>(File.ReadAllText(setPath), Json)
            ?? throw new InvalidDataException($"The set {setPath} is empty.");
        var root = RepositoryRoot();
        var differences = 0;

        foreach (var entry in set.Set)
        {
            var path = Path.Combine(root, entry.Source.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"{entry.Id}: the document is not there ({entry.Source}).");
                differences++;
                continue;
            }

            var sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(sha256, entry.Sha256, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{entry.Id}: the document is not the recorded one.");
                differences++;
                continue;
            }

            var readings = new List<ExtractionFile>(3);
            for (var repetition = 1; repetition <= 3; repetition++)
            {
                readings.Add(ExtractionFile.Of(PdfWordReader.Read(path, PageMargins.None), sha256));
            }

            if (readings.Any(reading => !reading.SameAs(readings[0])))
            {
                Console.Error.WriteLine($"{entry.Id}: the three readings differ.");
                differences++;
            }

            Directory.CreateDirectory(outDirectory);
            for (var repetition = 1; repetition <= 3; repetition++)
            {
                var target = Path.Combine(outDirectory, $"{Safe(entry.Id)}.r{repetition}.json");
                File.WriteAllBytes(target, JsonSerializer.SerializeToUtf8Bytes(readings[repetition - 1], Json));
            }
        }

        Console.WriteLine($"read {set.Set.Count} document(s), {differences} difference(s)");
        return differences == 0 ? ExitOk : ExitDifference;
    }

    /// <summary>Compares two folders of readings, word for word and page for page.</summary>
    private static int Compare(string measuredDirectory, string productDirectory)
    {
        var measured = Directory.EnumerateFiles(measuredDirectory, "*.r1.json").Order(StringComparer.Ordinal).ToList();
        if (measured.Count == 0)
        {
            throw new InvalidDataException($"No measured reading in {measuredDirectory}.");
        }

        var differences = 0;
        foreach (var file in measured)
        {
            var name = Path.GetFileName(file);
            var product = Path.Combine(productDirectory, name);
            if (!File.Exists(product))
            {
                Console.Error.WriteLine($"{name}: the product reader has no reading.");
                differences++;
                continue;
            }

            var expected = ExtractionFile.Read(file);
            var actual = ExtractionFile.Read(product);
            if (!expected.SameAs(actual))
            {
                Console.Error.WriteLine($"{name}: the product reader's reading differs.");
                differences++;
            }
        }

        Console.WriteLine($"compared {measured.Count} document(s), {differences} difference(s)");
        return differences == 0 ? ExitOk : ExitDifference;
    }

    private static string Safe(string id) => id.Replace('/', '_');

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dotnet-report-service.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidDataException("The repository root was not found.");
    }
}

/// <summary>The agreed set of documents, as the experiment recorded it.</summary>
internal sealed record PdfSet
{
    public List<PdfSetEntry> Set { get; init; } = [];
}

/// <summary>One document of the set.</summary>
internal sealed record PdfSetEntry
{
    public string Id { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>
/// What one reader read from one document, in the experiment's own format: the page
/// count, the page sizes and every word with its page and its box, in points, origin
/// top left, y downwards. Nothing about the time is stored, so repeated runs can be
/// compared byte for byte.
/// </summary>
internal sealed class ExtractionFile
{
    public string Reader { get; set; } = "pdfpig";

    public string Configuration { get; set; } = "a";

    public string PdfSha256 { get; set; } = string.Empty;

    public int Pages { get; set; }

    public List<double[]> PageSizes { get; set; } = [];

    public List<double[]> Boxes { get; set; } = [];

    public List<string> Texts { get; set; } = [];

    public Dictionary<string, string> Notes { get; set; } = [];

    public static ExtractionFile Of(PdfReading reading, string pdfSha256) => new()
    {
        PdfSha256 = pdfSha256,
        Pages = reading.Pages.Count,
        PageSizes = [.. reading.Pages.Select(page => new[] { page.Width, page.Height })],
        Boxes =
        [
            .. reading.Pages.SelectMany(page => page.Words.Select(word => new[] { (double)page.Number, word.Left, word.Top, word.Right, word.Bottom })),
        ],
        Texts = [.. reading.Pages.SelectMany(page => page.Words.Select(word => word.Text))],
    };

    public static ExtractionFile Read(string path) =>
        JsonSerializer.Deserialize<ExtractionFile>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        ?? throw new InvalidDataException($"The reading {path} is empty.");

    /// <summary>Whether both readings hold the same pages and the same words, in the same order.</summary>
    public bool SameAs(ExtractionFile other)
    {
        if (Pages != other.Pages || PageSizes.Count != other.PageSizes.Count)
        {
            return false;
        }

        for (var index = 0; index < PageSizes.Count; index++)
        {
            for (var figure = 0; figure < 2; figure++)
            {
                if (!Nearly(PageSizes[index][figure], other.PageSizes[index][figure]))
                {
                    return false;
                }
            }
        }

        if (Boxes.Count != other.Boxes.Count || Texts.Count != other.Texts.Count)
        {
            return false;
        }

        for (var index = 0; index < Boxes.Count; index++)
        {
            if (!string.Equals(Texts[index], other.Texts[index], StringComparison.Ordinal))
            {
                return false;
            }

            for (var figure = 0; figure < 5; figure++)
            {
                if (!Nearly(Boxes[index][figure], other.Boxes[index][figure]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Points are compared at the precision the experiment recorded them with.</summary>
    private static bool Nearly(double left, double right) =>
        Math.Abs(left - right) < 0.0005
        || string.Equals(left.ToString("0.####", CultureInfo.InvariantCulture), right.ToString("0.####", CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
