using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GroupContinuityCheck;
using PageAccountingCheck;
using PaginationFixtures;

namespace ReaderAgreement;

internal static class Clock
{
    public static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
}

internal static class Files
{
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Settings.Json) + "\n", new UTF8Encoding(false));
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256Text(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>The earlier experiments' recorded entries (their committed results), read as they are.</summary>
internal static class Records
{
    private static readonly Dictionary<string, Dictionary<string, JsonObject>> Cache = new(StringComparer.Ordinal);

    /// <summary>The run entry with the given id in a folder's results file (runs[].runId or runs[].run), or null.</summary>
    public static JsonObject? Find(string repositoryRoot, string folder, string file, string runId)
    {
        var key = $"{folder}/{file}";
        if (!Cache.TryGetValue(key, out var runs))
        {
            runs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var path = Path.Combine(repositoryRoot, "spikes", folder, "results", file);
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["runs"] is JsonArray array)
            {
                foreach (var item in array.OfType<JsonObject>())
                {
                    var id = (item["runId"] ?? item["run"])?.GetValue<string>();
                    if (id is not null)
                    {
                        runs.TryAdd(id, item);
                    }
                }
            }

            Cache[key] = runs;
        }

        return runs.GetValueOrDefault(runId);
    }
}

/// <summary>The inventory of committed PDFs and the measured set (Phase A.5).</summary>
internal static class Inventory
{
    public static int Run(string repositoryRoot, string resultsDirectory)
    {
        var committed = Directory.EnumerateFiles(Path.Combine(repositoryRoot, "spikes"), "*.pdf", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(repositoryRoot, p).Replace('\\', '/'))
            .Where(p => !p.Contains("/work/", StringComparison.Ordinal) && !p.Contains("/raw/", StringComparison.Ordinal) && !p.Contains("/out/", StringComparison.Ordinal)
                && !p.StartsWith("spikes/13-", StringComparison.Ordinal) && !p.StartsWith("spikes/14-", StringComparison.Ordinal) && !p.StartsWith("spikes/15-", StringComparison.Ordinal))
            .Where(p => p.Contains("/results/", StringComparison.Ordinal) || p.Contains("/fixtures/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var inventory = new List<object>();
        foreach (var path in committed)
        {
            var full = Path.Combine(repositoryRoot, path);
            var sha = Files.Sha256(full);
            var duplicateOf = byHash.GetValueOrDefault(sha);
            byHash.TryAdd(sha, path);
            var entry = PdfSet.All.FirstOrDefault(e => e.Source == path);
            inventory.Add(new
            {
                path,
                sha256 = sha,
                bytes = new FileInfo(full).Length,
                duplicateOf,
                measuredAs = entry?.Id,
                use = entry?.Use ?? (duplicateOf is not null ? "duplicate (measured once)" : "not measured"),
            });
        }

        var set = new List<object>();
        foreach (var e in PdfSet.All)
        {
            var full = e.Source is null ? null : Path.Combine(repositoryRoot, e.Source);
            var record = e.RecordFolder is null ? null : Records.Find(repositoryRoot, e.RecordFolder, "integrity.json", e.RecordRunId!);
            set.Add(new
            {
                e.Id, e.Source, e.Folder, e.Class, e.Use, e.Layout, e.FixtureId, e.Markup, e.Marks, e.Footer,
                e.RecordFolder, e.RecordRunId, e.StrictRecordFolder, e.StrictRecordRunId, e.KnownBad, e.Regenerated, e.Harness,
                sha256 = full is null ? null : Files.Sha256(full),
                bytes = full is null ? (long?)null : new FileInfo(full).Length,
                recorded = record is null ? null : new
                {
                    verdict = record["verdict"]?.GetValue<string>(),
                    pages = record["pages"]?.GetValue<int?>(),
                    failureClasses = record["failureClasses"]?.AsArray().Select(c => c!.GetValue<string>()).ToArray(),
                    contentSha256 = record["contentSha256"]?.GetValue<string>(),
                },
            });
        }

        Files.WriteJson(Path.Combine(resultsDirectory, "inventory.json"), new { writtenAt = Clock.Now(), committedPdfs = inventory.Count, distinct = byHash.Count, pdfs = inventory });
        Files.WriteJson(Path.Combine(resultsDirectory, "pdf-set.json"), new
        {
            pdfs = set.Count,
            judged = PdfSet.All.Count(e => e.Use == "judged"),
            agreementOnly = PdfSet.All.Count(e => e.Use == "agreement"),
            regenerated = PdfSet.All.Count(e => e.Regenerated),
            set,
        });
        Console.WriteLine($"{inventory.Count} committed PDFs ({byHash.Count} distinct); set of {set.Count}: {PdfSet.All.Count(e => e.Use == "judged")} judged, {PdfSet.All.Count(e => e.Use == "agreement")} agreement only.");
        var missingRecord = PdfSet.All.Where(e => e.Use == "judged" && Records.Find(repositoryRoot, e.RecordFolder!, "integrity.json", e.RecordRunId!) is null).ToList();
        foreach (var e in missingRecord)
        {
            Console.Error.WriteLine($"No recorded entry for {e.Id} ({e.RecordFolder} {e.RecordRunId}).");
        }

        return missingRecord.Count == 0 ? 0 : 1;
    }
}

/// <summary>Builds the shared fixtures and the group fixtures and checks both against the earlier lists.</summary>
internal static class FixturesCommand
{
    public static int Run(string repositoryRoot, string outDirectory)
    {
        var dataset = Path.Combine(repositoryRoot, "samples/datasets/header-items-v1.grouped.10k.json");
        var sums = Path.Combine(repositoryRoot, "samples/datasets/SHA256SUMS");
        var series = Path.Combine(outDirectory, "series");
        var shared = Path.Combine(outDirectory, "shared");
        var groups = Path.Combine(outDirectory, "groups");

        // 1. The shared series of the native experiment, checked against its list.
        Directory.CreateDirectory(series);
        var builder = new FixtureSeries(Dataset.Load(dataset, sums));
        foreach (var id in FixtureSeries.AllIds)
        {
            FixtureFile.Write(series, builder.Build(id));
        }

        Sums.WriteFolder(series, Path.Combine(series, FixtureFile.SumsFileName));
        var failures = Compare(Path.Combine(repositoryRoot, "spikes/07-native-tall-rows/results/fixtures.sha256"), Path.Combine(series, FixtureFile.SumsFileName));

        // 2. The ordinary blocks of the two-pass experiment, added to a copy of the series, checked against its list.
        if (OrdinaryFixtures.Run(dataset, sums, series, shared) != 0)
        {
            Console.Error.WriteLine("The ordinary block builder failed.");
            return 1;
        }

        failures += Compare(Path.Combine(repositoryRoot, "spikes/09-two-pass-page-subtotals/results/fixtures.sha256"), Path.Combine(shared, FixtureFile.SumsFileName));

        // 3. The group fixtures of the group experiment, checked against its list.
        if (GroupFixtures.Run(dataset, sums, shared, Path.Combine(shared, "T300u.json"), groups) != 0)
        {
            Console.Error.WriteLine("The group fixture builder failed.");
            return 1;
        }

        failures += Compare(Path.Combine(repositoryRoot, "spikes/12-group-continuity/results/fixtures.sha256"), Path.Combine(groups, "fixtures", FixtureFile.SumsFileName));
        Console.WriteLine(failures == 0 ? "Fixtures identical to the three earlier lists." : $"{failures} fixture differences.");
        return failures == 0 ? 0 : 1;
    }

    private static int Compare(string expectedSums, string actualSums)
    {
        var expected = Sums.Read(expectedSums);
        var actual = Sums.Read(actualSums);
        var failures = 0;
        foreach (var (name, hash) in expected)
        {
            if (actual.GetValueOrDefault(name) != hash)
            {
                Console.Error.WriteLine($"{name}: expected {hash}, built {actual.GetValueOrDefault(name) ?? "nothing"}.");
                failures++;
            }
        }

        return failures;
    }

    /// <summary>Reads a fixture from the shared folder (series and ordinary blocks) or the group folder, checked against that folder's list.</summary>
    public static Fixture Load(string fixturesDirectory, string id)
    {
        foreach (var sub in new[] { "shared", Path.Combine("groups", "fixtures") })
        {
            var dir = Path.Combine(fixturesDirectory, sub);
            var sums = Path.Combine(dir, FixtureFile.SumsFileName);
            if (File.Exists(FixtureFile.PathOf(dir, id)) && File.Exists(sums))
            {
                return FixtureFile.ReadVerified(dir, id, Sums.Read(sums));
            }
        }

        throw new InvalidDataException($"Fixture {id} not found.");
    }
}

/// <summary>results/freeze.json: the values fixed before the first run, and the check every stage makes against it.</summary>
internal static class Freeze
{
    public static int Write(string resultsDirectory)
    {
        var settings = JsonSerializer.Serialize(Settings.Describe(), Settings.JsonLine);
        var set = JsonSerializer.Serialize(PdfSet.All, Settings.JsonLine);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "licences.json", "identity.sha256", "pdf-set.json", "inventory.json" })
        {
            var path = Path.Combine(resultsDirectory, name);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"{name} must exist before the freeze.");
                return 1;
            }

            files[name] = Files.Sha256(path);
        }

        Files.WriteJson(Path.Combine(resultsDirectory, "freeze.json"), new
        {
            frozenAt = Clock.Now(),
            settings = JsonNode.Parse(settings),
            settingsSha256 = Files.Sha256Text(settings),
            pdfSetSha256 = Files.Sha256Text(set),
            files,
        });
        Console.WriteLine("Frozen.");
        return 0;
    }

    /// <summary>Null when the frozen values equal the current ones; otherwise the difference.</summary>
    public static string? Check(string resultsDirectory)
    {
        var path = Path.Combine(resultsDirectory, "freeze.json");
        if (!File.Exists(path))
        {
            return "No results/freeze.json: nothing runs before the freeze.";
        }

        var frozen = JsonNode.Parse(File.ReadAllText(path))!;
        var settings = Files.Sha256Text(JsonSerializer.Serialize(Settings.Describe(), Settings.JsonLine));
        var set = Files.Sha256Text(JsonSerializer.Serialize(PdfSet.All, Settings.JsonLine));
        if (frozen["settingsSha256"]?.GetValue<string>() != settings)
        {
            return "The settings differ from the frozen settings.";
        }

        if (frozen["pdfSetSha256"]?.GetValue<string>() != set)
        {
            return "The PDF set differs from the frozen set.";
        }

        foreach (var (name, hash) in frozen["files"]!.AsObject())
        {
            var file = Path.Combine(resultsDirectory, name);
            if (!File.Exists(file) || Files.Sha256(file) != hash!.GetValue<string>())
            {
                return $"{name} differs from its frozen hash.";
            }
        }

        return null;
    }
}

/// <summary>results/regenerated.json: where each regenerated PDF is, written after the regeneration.</summary>
internal static class Regeneration
{
    public static int Locate(string repositoryRoot, string regeneratedDirectory, string resultsDirectory, string images)
    {
        var found = new List<RegeneratedPdf>();
        var files = Directory.Exists(regeneratedDirectory)
            ? Directory.EnumerateFiles(regeneratedDirectory, "*.pdf", SearchOption.AllDirectories).ToList()
            : [];
        foreach (var e in PdfSet.All.Where(e => e.Regenerated))
        {
            var file = files.Where(f => Path.GetFileName(f) == e.RecordRunId + ".pdf"
                    && f.Replace('\\', '/').Contains("/" + e.Harness + "/", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
            found.Add(new RegeneratedPdf
            {
                Id = e.Id,
                Harness = e.Harness,
                HarnessImage = images,
                Path = file is null ? null : Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/'),
                Sha256 = file is null ? null : Files.Sha256(file),
                Bytes = file is null ? null : new FileInfo(file).Length,
            });
        }

        Files.WriteJson(Path.Combine(resultsDirectory, "regenerated.json"), new { writtenAt = Clock.Now(), pdfs = found });
        Console.WriteLine($"{found.Count(f => f.Path is not null)} of {found.Count} regenerated PDFs found.");
        return found.All(f => f.Path is not null) ? 0 : 1;
    }

    public static Dictionary<string, RegeneratedPdf> Read(string resultsDirectory)
    {
        var path = Path.Combine(resultsDirectory, "regenerated.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, RegeneratedPdf>(StringComparer.Ordinal);
        }

        var node = JsonNode.Parse(File.ReadAllText(path))!["pdfs"]!;
        return node.Deserialize<List<RegeneratedPdf>>(Settings.Json)!.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }
}
