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

    internal static readonly JsonSerializerOptions Json = new()
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
        var regenerated = RegeneratedIndex.Load(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(setPath))!, "regenerated.json"));
        var root = RepositoryRoot();
        var differences = 0;

        foreach (var entry in set.Set)
        {
            // A regenerated document has no source of its own: the run that produced it
            // recorded its path and hash beside the set.
            var source = entry.Source ?? string.Empty;
            var sha256 = entry.Sha256 ?? string.Empty;
            if (entry.Regenerated)
            {
                var recorded = regenerated.Find(entry.Id);
                source = recorded?.Path ?? string.Empty;
                sha256 = recorded?.Sha256 ?? string.Empty;
            }

            var path = Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(source) || !File.Exists(path))
            {
                Console.Error.WriteLine($"{entry.Id}: the document is not there ({source}).");
                differences++;
                continue;
            }

            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(actual, sha256, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"{entry.Id}: the document is not the recorded one.");
                differences++;
                continue;
            }

            var readings = new List<ExtractionFile>(3);
            for (var repetition = 1; repetition <= 3; repetition++)
            {
                readings.Add(ExtractionFile.Of(PdfWordReader.Read(path, PageMargins.None), actual));
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

    /// <summary>Compares two folders of readings, word for word and page for page, repetition by repetition.</summary>
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
            var stem = Path.GetFileName(file)[..^".r1.json".Length];
            for (var repetition = 1; repetition <= 3; repetition++)
            {
                var name = $"{stem}.r{repetition}.json";
                var product = Path.Combine(productDirectory, name);
                if (!File.Exists(product))
                {
                    Console.Error.WriteLine($"{name}: the product reader has no reading.");
                    differences++;
                    continue;
                }

                var expected = ExtractionFile.Read(Path.Combine(measuredDirectory, name));
                var actual = ExtractionFile.Read(product);
                if (!expected.SameAs(actual))
                {
                    Console.Error.WriteLine($"{name}: the product reader's reading differs.");
                    differences++;
                }
            }
        }

        Console.WriteLine($"compared {measured.Count} document(s) over three repetitions, {differences} difference(s)");
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

    public string? Source { get; init; }

    public string? Sha256 { get; init; }

    /// <summary>Whether the document is one the original harnesses regenerated.</summary>
    public bool Regenerated { get; init; }
}

/// <summary>
/// The regenerated documents the harness runs produced, as the run recorded them:
/// where each PDF is, relative to the repository root, and its hash.
/// </summary>
internal sealed record RegeneratedIndex
{
    public List<RegeneratedPdf> Pdfs { get; init; } = [];

    /// <summary>Reads the index beside the set, or an empty one when the set holds none.</summary>
    /// <param name="path">The <c>regenerated.json</c> beside the set.</param>
    public static RegeneratedIndex Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<RegeneratedIndex>(File.ReadAllText(path), Program.Json) ?? new RegeneratedIndex()
            : new RegeneratedIndex();

    /// <summary>The record of <paramref name="id"/>, or null when the regeneration never produced it.</summary>
    /// <param name="id">The document's id in the set.</param>
    public RegeneratedPdf? Find(string id) =>
        Pdfs.FirstOrDefault(pdf => string.Equals(pdf.Id, id, StringComparison.Ordinal));
}

/// <summary>One regenerated document: its id, its path from the repository root and its hash.</summary>
internal sealed record RegeneratedPdf
{
    public string Id { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

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
        Reader = "pdfpig",
        Configuration = "pdfpig-a",
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

    /// <summary>
    /// Whether both readings hold the same pages, the same page sizes and, on each page,
    /// the same words with the same boxes. The order of the words is not compared: the
    /// measured extractor lists them in the library's own order, the product's reader in
    /// the content order the verification compares in, and the measured files record no
    /// content order to compare with. Every other value of every word is compared.
    /// </summary>
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

        for (var page = 1; page <= Pages; page++)
        {
            var mine = Words(page);
            var theirs = other.Words(page);
            if (mine.Count != theirs.Count)
            {
                return false;
            }

            foreach (var group in mine.GroupBy(word => word.Text, StringComparer.Ordinal))
            {
                var same = theirs.Where(word => string.Equals(word.Text, group.Key, StringComparison.Ordinal)).ToList();
                var left = group.OrderBy(word => (word.Box[0], word.Box[1], word.Box[2], word.Box[3])).ToList();
                if (same.Count != left.Count)
                {
                    return false;
                }

                var right = same.OrderBy(word => (word.Box[0], word.Box[1], word.Box[2], word.Box[3])).ToList();
                for (var index = 0; index < left.Count; index++)
                {
                    for (var figure = 0; figure < 4; figure++)
                    {
                        if (!Nearly(left[index].Box[figure], right[index].Box[figure]))
                        {
                            return false;
                        }
                    }
                }
            }
        }

        return true;
    }

    private List<(string Text, double[] Box)> Words(int page)
    {
        var words = new List<(string Text, double[] Box)>();
        for (var index = 0; index < Boxes.Count; index++)
        {
            if (Boxes[index][0] != page)
            {
                continue;
            }

            words.Add((Texts[index], [Boxes[index][1], Boxes[index][2], Boxes[index][3], Boxes[index][4]]));
        }

        return words;
    }

    /// <summary>Points are compared at the precision the experiment recorded them with.</summary>
    private static bool Nearly(double left, double right) =>
        Math.Abs(left - right) < 0.0005
        || string.Equals(left.ToString("0.####", CultureInfo.InvariantCulture), right.ToString("0.####", CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
