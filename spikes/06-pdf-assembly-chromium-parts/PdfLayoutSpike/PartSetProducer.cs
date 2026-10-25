using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfLayoutSpike;

/// <summary>
/// One layout tried while searching for a part's record count: the ordinary records
/// laid out (and the last record's sequence), the run status, the pages the layout
/// reported, the pages counted in the PDF, and the time.
/// </summary>
internal sealed record LayoutAttempt(int Records, int LastSequence, string Status, int? LayoutPages, int? PdfPagesHeuristic, double Seconds);

/// <summary>One accepted part: its place in the set, its records and how it was found.</summary>
internal sealed record ProducedPart(
    int Order,
    string File,
    int Pages,
    int FirstGlobalPage,
    string Sha256,
    int FirstSequence,
    int LastSequence,
    int Records,
    int Layouts,
    int ExcludedCount,
    IReadOnlyList<int> ExcludedSequences);

/// <summary>Production record of one part: every layout tried, the accepted checks, time and size.</summary>
internal sealed record PartProduction(
    int Order,
    string File,
    int TargetPages,
    int FirstSequence,
    int LastSequence,
    int Records,
    int ExcludedCount,
    IReadOnlyList<int> ExcludedSequences,
    IReadOnlyList<LayoutAttempt> Attempts,
    IReadOnlyList<CheckResult> Checks,
    double Seconds,
    long Bytes);

/// <summary>
/// Which records are left out of every part: the rule, its threshold, how many
/// records it excludes and the description lengths on either side of it.
/// </summary>
internal sealed record ExclusionInfo(
    string Rule,
    int Threshold,
    int ExcludedCount,
    int OrdinaryRecords,
    int LargestBelow,
    int SmallestAtOrAbove);

/// <summary>The excluded records whose sequences fall inside a range of the dataset.</summary>
internal sealed record ExcludedInRange(int FirstSequence, int LastSequence, int Count, IReadOnlyList<int> Sequences);

/// <summary>What printed the parts, stated exactly.</summary>
internal sealed record ProducerInfo(
    string CodeCommit,
    string PagedJsVersion,
    string PagedJsSha256,
    string Configuration,
    string Footer,
    string PlaywrightVersion,
    string ChromiumVersion,
    string BaseImageDigest,
    string Font,
    string DotnetVersion);

/// <summary>The dataset the record slices come from, and the records left out of every part.</summary>
internal sealed record DatasetInfo(string File, string Sha256, int Records, ExclusionInfo Exclusion);

/// <summary>A set of partial PDFs in the shape the assembly experiment reads, plus the producer's fields.</summary>
internal sealed record ProducedSet(
    ulong Seed,
    int TargetPages,
    IReadOnlyList<ProducedPart> Parts,
    ProducerInfo Producer,
    DatasetInfo Dataset,
    ExcludedInRange ExcludedInUsedRange);

/// <summary>Thrown when a part cannot be produced or accepted, or the page-count lists differ from the earlier ones.</summary>
internal sealed class ProductionException(string message) : Exception(message);

/// <summary>
/// Prints sets of partial PDFs with the layout harness. Only ordinary records are
/// used: a record whose description has 1 500 characters or more (UTF-16 code
/// units) is left out of every part. Each part is a contiguous slice of the
/// remaining records, in dataset order with their sequence numbers unchanged,
/// whose record count is searched until its layout gives exactly the part's page
/// count; every part must pass the harness's ordinary-block checks before it is
/// used, and every layout tried is recorded.
/// </summary>
internal sealed class PartSetProducer
{
    public const ulong DefaultSeed = 20261021;
    public const int MinPagesPerPart = 10;
    public const int MaxPagesPerPart = 30;
    public const int MaxLayoutsPerPart = 16;

    /// <summary>A record is ordinary exactly when its description is shorter than this (UTF-16 code units).</summary>
    public const int OrdinaryDescriptionLimit = 1500;

    public const string ExclusionRule =
        "A record is ordinary exactly when its description has fewer than 1500 UTF-16 code units; every other record is left out of every part.";

    private readonly BlockSource source;
    private readonly BlockHtmlBuilder builder;
    private readonly LayoutRunner runner;
    private readonly string workDirectory;
    private readonly int timeoutSeconds;
    private readonly string font;

    /// <summary>The ordinary records, in dataset order: the sequence every part is sliced from.</summary>
    private readonly List<DatasetRecord> ordinary;

    /// <summary>The sequences of the records left out, ascending.</summary>
    private readonly List<int> excluded;

    public PartSetProducer(BlockSource source, BlockHtmlBuilder builder, LayoutRunner runner, string workDirectory, int timeoutSeconds, string font)
    {
        this.source = source;
        this.builder = builder;
        this.runner = runner;
        this.workDirectory = workDirectory;
        this.timeoutSeconds = timeoutSeconds;
        this.font = font;
        ordinary = [.. source.Records.Where(IsOrdinary)];
        excluded = [.. source.Records.Where(r => !IsOrdinary(r)).Select(r => r.Sequence).Order()];
        Exclusion = new ExclusionInfo(
            ExclusionRule,
            OrdinaryDescriptionLimit,
            excluded.Count,
            ordinary.Count,
            ordinary.Count == 0 ? 0 : ordinary.Max(r => r.Description.Length),
            excluded.Count == 0 ? 0 : source.Records.Where(r => !IsOrdinary(r)).Min(r => r.Description.Length));
    }

    /// <summary>The exclusion as the set manifest records it.</summary>
    public ExclusionInfo Exclusion { get; }

    public static bool IsOrdinary(DatasetRecord record) => record.Description.Length < OrdinaryDescriptionLimit;

    /// <summary>Records per page of the layout harness's 1 000-record block (78 pages): the first guess of the first part.</summary>
    public const double FirstRecordsPerPage = 1000.0 / 78.0;

    public static readonly RunConfiguration Configuration = new(LayoutMode.Paged, RowMarkup.Table, KeepPolicy.Keep);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // The page-count draw and the file names of the earlier assembly experiment, copied verbatim.
    public static int PartsFor(int targetPages) => targetPages switch
    {
        1000 => 50,
        5000 => 250,
        10000 => 500,
        _ => Math.Max(1, targetPages / 20),
    };

    /// <summary>Draws 10–30 pages per part, then adjusts single parts until the sum is exactly the target.</summary>
    public static int[] PageCounts(int targetPages, int partCount, SplitMix64 random)
    {
        if (targetPages < partCount * MinPagesPerPart || targetPages > partCount * MaxPagesPerPart)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPages), $"{targetPages} pages cannot be split into {partCount} parts of {MinPagesPerPart}-{MaxPagesPerPart} pages.");
        }

        int[] counts = new int[partCount];
        for (int i = 0; i < partCount; i++)
        {
            counts[i] = random.NextInt(MinPagesPerPart, MaxPagesPerPart + 1);
        }

        int sum = counts.Sum();
        while (sum != targetPages)
        {
            int index = random.NextInt(0, partCount);
            if (sum < targetPages && counts[index] < MaxPagesPerPart)
            {
                counts[index]++;
                sum++;
            }
            else if (sum > targetPages && counts[index] > MinPagesPerPart)
            {
                counts[index]--;
                sum--;
            }
        }

        return counts;
    }

    public static string FileName(ulong seed, int targetPages, int order)
    {
        string key = string.Create(CultureInfo.InvariantCulture, $"part|{seed}|{targetPages}|{order}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".pdf";
    }

    /// <summary>The page counts of a set, drawn exactly as the earlier experiment drew them.</summary>
    public static int[] DrawPageCounts(int targetPages, ulong seed)
    {
        var setRandom = new SplitMix64(seed ^ ((ulong)targetPages * 0xD6E8FEB86659FD93UL));
        return PageCounts(targetPages, PartsFor(targetPages), setRandom);
    }

    /// <summary>
    /// Compares the (order, file, pages) list of a set with the list the earlier
    /// experiment committed for the same size. Throws on any difference.
    /// </summary>
    public static void CrossCheck(int targetPages, ulong seed, int[] counts, string baselineVerificationPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(baselineVerificationPath));
        string key = targetPages.ToString(CultureInfo.InvariantCulture);
        if (!document.RootElement.GetProperty("sourceParts").TryGetProperty(key, out var list))
        {
            throw new ProductionException($"The earlier list has no set of {key} pages.");
        }

        var earlier = list.EnumerateArray()
            .Select(e => (Order: e.GetProperty("order").GetInt32(), File: e.GetProperty("file").GetString() ?? string.Empty, Pages: e.GetProperty("manifestPages").GetInt32()))
            .ToList();
        if (earlier.Count != counts.Length)
        {
            throw new ProductionException($"Set {key}: {counts.Length} parts, the earlier list has {earlier.Count}.");
        }

        for (int i = 0; i < counts.Length; i++)
        {
            var mine = (Order: i + 1, File: FileName(seed, targetPages, i + 1), Pages: counts[i]);
            if (mine != earlier[i])
            {
                throw new ProductionException($"Set {key}, entry {i + 1}: ({mine.Order}, {mine.File}, {mine.Pages}) differs from the earlier ({earlier[i].Order}, {earlier[i].File}, {earlier[i].Pages}).");
            }
        }
    }

    /// <summary>
    /// Produces one set into setDirectory: the accepted parts, manifest.json and
    /// production.json. When a part cannot be produced or accepted, every layout tried
    /// for it is printed and written to production-failure.json in the set folder,
    /// and production stops.
    /// </summary>
    public async Task<string> ProduceAsync(int targetPages, ulong seed, int[] counts, string setDirectory, ProducerInfo producer)
    {
        if (Directory.Exists(setDirectory))
        {
            Directory.Delete(setDirectory, recursive: true);
        }

        Directory.CreateDirectory(setDirectory);
        foreach (string folder in new[] { "pdf", "layout" })
        {
            Directory.CreateDirectory(Path.Combine(workDirectory, folder));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Ordinary records: {ordinary.Count} of {source.Records.Count}; {excluded.Count} left out (descriptions of {OrdinaryDescriptionLimit} characters or more; longest kept {Exclusion.LargestBelow}, shortest left out {Exclusion.SmallestAtOrAbove})."));
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var parts = new List<ProducedPart>(counts.Length);
        var productions = new List<PartProduction>(counts.Length);
        int nextIndex = 0; // index into the ordinary records of the next part's first record
        int nextGlobal = 1;
        double recordsPerPage = FirstRecordsPerPage;
        for (int i = 0; i < counts.Length; i++)
        {
            int order = i + 1;
            string file = FileName(seed, targetPages, order);
            var partClock = Stopwatch.StartNew();
            var attempts = new List<LayoutAttempt>();
            int records;
            RunResult run;
            try
            {
                (records, run) = await SearchAsync(order, counts[i], nextIndex, recordsPerPage, attempts);
            }
            catch (ProductionException e)
            {
                WriteFailure(setDirectory, targetPages, seed, order, file, counts[i], nextIndex, attempts, parts.Count, e.Message);
                throw;
            }

            var block = SliceBlock(order, nextIndex, records);
            int firstSequence = block.Records[0].Sequence;
            int lastSequence = block.Records[^1].Sequence;
            var partExcluded = ExcludedBetween(firstSequence, lastSequence);
            string target = Path.Combine(setDirectory, file);
            File.Copy(Path.Combine(workDirectory, run.PdfFile!), target, overwrite: true);
            DeleteRunFiles(run.Id);
            byte[] bytes = File.ReadAllBytes(target);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            parts.Add(new ProducedPart(order, file, counts[i], nextGlobal, sha256,
                firstSequence, lastSequence, records, attempts.Count, partExcluded.Count, partExcluded));
            productions.Add(new PartProduction(order, file, counts[i], firstSequence, lastSequence, records, partExcluded.Count, partExcluded,
                attempts, run.Checks, Math.Round(partClock.Elapsed.TotalSeconds, 2), bytes.LongLength));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"part {order:D4} {file}: {counts[i]} pages, records {firstSequence}-{lastSequence} ({records} ordinary, {partExcluded.Count} left out), {attempts.Count} layouts ({string.Join(" ", attempts.Select(a => $"{a.Records}->{a.LayoutPages}"))}), {partClock.Elapsed.TotalSeconds:F1} s, {bytes.LongLength} bytes"));
            nextIndex += records;
            nextGlobal += counts[i];
            recordsPerPage = (double)records / counts[i];
        }

        int usedFirst = parts[0].FirstSequence;
        int usedLast = parts[^1].LastSequence;
        var setExcluded = ExcludedBetween(usedFirst, usedLast);
        var excludedInUsedRange = new ExcludedInRange(usedFirst, usedLast, setExcluded.Count, setExcluded);
        var set = new ProducedSet(seed, targetPages, parts, producer,
            new DatasetInfo(source.FileName, source.Sha256, source.Records.Count, Exclusion), excludedInUsedRange);
        string manifestPath = Path.Combine(setDirectory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(set, Json) + "\n");
        var production = new
        {
            targetPages,
            seed,
            started = started.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            finished = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            wallSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
            parts = productions.Count,
            recordsUsed = parts.Sum(p => p.Records),
            exclusion = Exclusion,
            excludedInUsedRange = new { firstSequence = usedFirst, lastSequence = usedLast, count = setExcluded.Count },
            layoutsTried = productions.Sum(p => p.Attempts.Count),
            bytes = productions.Sum(p => p.Bytes),
            font,
            configuration = Configuration.Name,
            producer,
            perPart = productions,
        };
        File.WriteAllText(Path.Combine(setDirectory, "production.json"), JsonSerializer.Serialize(production, Json) + "\n");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"set {targetPages}: {parts.Count} parts, {parts.Sum(p => p.Pages)} pages, {parts.Sum(p => p.Records)} ordinary records (sequences {usedFirst}-{usedLast}, {setExcluded.Count} left out in that range), {productions.Sum(p => p.Attempts.Count)} layouts, {clock.Elapsed.TotalSeconds:F1} s"));
        return manifestPath;
    }

    /// <summary>The sequences of the excluded records from first to last, both included, ascending.</summary>
    private List<int> ExcludedBetween(int first, int last) => [.. excluded.Where(s => s >= first && s <= last)];

    /// <summary>
    /// Prints every layout tried for the part that could not be produced and writes
    /// them, with the part's place, its target, its first ordinary record and the
    /// excluded records inside the span tried, to production-failure.json.
    /// </summary>
    private void WriteFailure(string setDirectory, int setPages, ulong seed, int order, string file, int targetPages, int firstIndex, List<LayoutAttempt> attempts, int partsAccepted, string reason)
    {
        int firstSequence = firstIndex < ordinary.Count ? ordinary[firstIndex].Sequence : -1;
        int lastTried = attempts.Count == 0 ? firstSequence : attempts.Max(a => a.LastSequence);
        List<int> spanExcluded = firstSequence < 0 ? [] : ExcludedBetween(firstSequence, lastTried);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"part {order} {file}: target {targetPages} pages, first ordinary record {firstSequence}; {attempts.Count} layouts tried:"));
        foreach (var a in attempts)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {a.Records} records (sequences {firstSequence}-{a.LastSequence}): {a.Status}, layout pages {a.LayoutPages?.ToString(CultureInfo.InvariantCulture) ?? "-"}, PDF pages {a.PdfPagesHeuristic?.ToString(CultureInfo.InvariantCulture) ?? "-"}, {a.Seconds:F2} s"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  excluded records inside {firstSequence}-{lastTried}: {spanExcluded.Count} ({string.Join(", ", spanExcluded)})"));
        var failure = new
        {
            setPages,
            seed,
            order,
            file,
            targetPages,
            firstOrdinarySequence = firstSequence,
            partsAcceptedBefore = partsAccepted,
            reason,
            threshold = OrdinaryDescriptionLimit,
            attempts,
            spanTried = new ExcludedInRange(firstSequence, lastTried, spanExcluded.Count, spanExcluded),
            recordedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
        };
        File.WriteAllText(Path.Combine(setDirectory, "production-failure.json"), JsonSerializer.Serialize(failure, Json) + "\n");
    }

    /// <summary>
    /// Finds the ordinary-record count whose layout gives exactly the target page
    /// count: a first guess from the previous part's records per page, then steps
    /// until the target is bracketed, then bisection; at most 16 layouts. Every
    /// layout tried is appended to attempts, also when the search fails.
    /// </summary>
    private async Task<(int Records, RunResult Run)> SearchAsync(int order, int targetPages, int firstIndex, double recordsPerPage, List<LayoutAttempt> attempts)
    {
        int available = ordinary.Count - firstIndex;
        int? below = null; // largest record count known to give fewer pages
        int? above = null; // smallest record count known to give more pages
        int guess = Math.Clamp((int)Math.Round(targetPages * recordsPerPage, MidpointRounding.AwayFromZero), 1, available);
        while (attempts.Count < MaxLayoutsPerPart)
        {
            var run = await LayoutAsync(order, firstIndex, guess);
            attempts.Add(new LayoutAttempt(guess, ordinary[firstIndex + guess - 1].Sequence, run.Status, run.Layout?.Total, run.PdfPagesHeuristic,
                Math.Round(run.LayoutMs is null ? 0 : (run.NavigationMs + run.LayoutMs.Value + (run.PdfMs ?? 0)) / 1000, 2)));
            if (run.Status != "ok" || run.Pages is null)
            {
                DeleteRunFiles(run.Id);
                throw new ProductionException($"part {order}: layout of {guess} records ended with status {run.Status}: {run.Message}");
            }

            int pages = run.Pages.Value;
            if (pages == targetPages)
            {
                Accept(order, run, targetPages);
                return (guess, run);
            }

            DeleteRunFiles(run.Id);
            if (pages < targetPages)
            {
                below = Math.Max(below ?? 0, guess);
            }
            else
            {
                above = Math.Min(above ?? int.MaxValue, guess);
            }

            int next;
            if (below is not null && above is not null)
            {
                if (above.Value - below.Value <= 1)
                {
                    throw new ProductionException($"part {order}: no record count gives exactly {targetPages} pages ({below} records give fewer, {above} give more).");
                }

                next = below.Value + ((above.Value - below.Value) / 2);
            }
            else
            {
                int step = Math.Max(1, (int)Math.Round(Math.Abs(targetPages - pages) * (double)guess / pages, MidpointRounding.AwayFromZero));
                next = pages < targetPages ? guess + step : guess - step;
            }

            if (next < 1 || next > available)
            {
                throw new ProductionException($"part {order}: the search for {targetPages} pages left the ordinary records ({next} records from ordinary record {firstIndex + 1}).");
            }

            guess = next;
        }

        throw new ProductionException($"part {order}: no exact layout of {targetPages} pages within {MaxLayoutsPerPart} layouts.");
    }

    /// <summary>The layout harness's ordinary-block checks; a part that fails any of them is never used.</summary>
    private static void Accept(int order, RunResult run, int targetPages)
    {
        var failures = new List<string>();
        if (run.Layout?.Total != targetPages || run.PdfPagesHeuristic != targetPages)
        {
            failures.Add($"page counts layout {run.Layout?.Total} / PDF {run.PdfPagesHeuristic}, expected {targetPages}");
        }

        string[] required =
        [
            AccountingVerifier.Rows, AccountingVerifier.EndPageSubtotals, AccountingVerifier.Chain, AccountingVerifier.VisibleAmounts,
            AccountingVerifier.Marks, AccountingVerifier.Geometry, AccountingVerifier.Escaping,
        ];
        foreach (string name in required)
        {
            if (!run.Checks.Any(c => c.Name == name))
            {
                failures.Add($"check {name} missing");
            }
        }

        foreach (var check in run.Checks.Where(c => !c.Name.EndsWith("-observation", StringComparison.Ordinal) && !c.Pass))
        {
            failures.Add($"check {check.Name} failed on page {check.FirstFailingPage}: {check.Detail}");
        }

        if (AccountingVerifier.LogsLayoutRepeated(run))
        {
            failures.Add("the console logs \"Layout repeated\"");
        }

        if (run.ConsoleMessages.Count > 0)
        {
            failures.Add("console warning or error: " + run.ConsoleMessages[0]);
        }

        if (failures.Count > 0)
        {
            throw new ProductionException($"part {order} ({run.Id}) is not accepted: {string.Join("; ", failures)}");
        }
    }

    private async Task<RunResult> LayoutAsync(int order, int firstIndex, int count)
    {
        var block = SliceBlock(order, firstIndex, count);
        string id = string.Create(CultureInfo.InvariantCulture, $"P{order:D4}-n{count}");
        var run = new RunResult
        {
            Id = id,
            Configuration = Configuration.Name,
            Block = block.Name,
            Footer = true,
            Repetition = 1,
            Purpose = "part",
        };
        var (html, scripts) = builder.Build(source, block, Configuration, footer: true, id);
        run.ScriptsExpected = scripts;
        try
        {
            await runner.RunAsync(run, html, Configuration.Mode, workDirectory, timeoutSeconds);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            run.Status = "error";
            run.Message = e.GetType().Name + ": " + e.Message.Split('\n')[0];
        }

        if (run.Status == "ok")
        {
            run.Checks = AccountingVerifier.VerifyPaged(block, run);
        }

        return run;
    }

    /// <summary>
    /// The block of the ordinary records [firstIndex, firstIndex + count), built with
    /// the same row rules as the layout harness's own block builder, applied to the
    /// ordinary records: a group start before the first record and before each record
    /// whose group differs from the previous ordinary record; a group end with the
    /// group's total over the block's records of that group after a record whose next
    /// ordinary record belongs to another group or does not exist.
    /// </summary>
    public Block SliceBlock(int order, int firstIndex, int count)
    {
        var records = ordinary.GetRange(firstIndex, count);
        var nextAfterBlock = firstIndex + count < ordinary.Count ? ordinary[firstIndex + count] : null;
        var rows = new List<BlockRow>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (i == 0 || records[i - 1].GroupId != record.GroupId)
            {
                rows.Add(new BlockRow(BlockRowKind.GroupStart, null, record.GroupId, record.GroupName, 0m));
            }

            rows.Add(new BlockRow(BlockRowKind.Item, record, record.GroupId, record.GroupName, 0m));
            var next = i + 1 < records.Count ? records[i + 1] : nextAfterBlock;
            if (next is null || next.GroupId != record.GroupId)
            {
                var start = i;
                while (start > 0 && records[start - 1].GroupId == record.GroupId)
                {
                    start--;
                }

                var total = records.Skip(start).Take(i - start + 1).Sum(r => r.Amount);
                rows.Add(new BlockRow(BlockRowKind.GroupEnd, null, record.GroupId, record.GroupName, total));
            }
        }

        return new Block(string.Create(CultureInfo.InvariantCulture, $"P{order:D4}"), records, rows);
    }

    private void DeleteRunFiles(string id)
    {
        foreach (string path in new[] { Path.Combine(workDirectory, "pdf", id + ".pdf"), Path.Combine(workDirectory, "layout", id + ".json") })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
