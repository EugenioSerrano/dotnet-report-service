using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReaderAgreement;

/// <summary>
/// Every value fixed before the first run: the agreement tolerance, the caps,
/// the ceilings, the repetitions and the reader configurations. They are
/// written to results/freeze.json, and a stage refuses to start when the
/// frozen values differ from these.
/// </summary>
internal static class Settings
{
    /// <summary>Largest accepted difference of a matched word's left or right edge, in points.</summary>
    public const double HorizontalTolerancePt = 1.0;

    /// <summary>Largest accepted difference of a matched word's vertical centre, in points.</summary>
    public const double VerticalCentreTolerancePt = 3.0;

    /// <summary>Margin added around a stamped page number's words to form its area, in points.</summary>
    public const double StampAreaMarginPt = 2.0;

    public const int RunCapSeconds = 300;

    public const int KillAfterSeconds = 330;

    public const string ContainerMemory = "4g";

    public const string ContainerMemorySwap = "4g";

    /// <summary>Near-ceiling: a first repetition slower than this skips the remaining repetitions.</summary>
    public const int NearCeilingSeconds = 240;

    /// <summary>Near-ceiling: a process tree above this skips the remaining repetitions (3.2 GiB).</summary>
    public const long NearCeilingTreeKiB = 3_355_443;

    /// <summary>No run starts once the work folder exceeds this size (4 GiB).</summary>
    public const long WorkFolderCeilingBytes = 4L * 1024 * 1024 * 1024;

    public const int StageBudgetSeconds = 3600;

    public const int SamplingIntervalMilliseconds = 50;

    public static readonly IReadOnlyDictionary<string, int> Repetitions = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [Readers.Oracle] = 3,
        [Readers.PdfPig] = 3,
        [Readers.PdfSharp] = 1,
    };

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    /// <summary>The frozen settings, in the order they are written.</summary>
    public static object Describe() => new
    {
        horizontalTolerancePt = HorizontalTolerancePt,
        verticalCentreTolerancePt = VerticalCentreTolerancePt,
        stampAreaMarginPt = StampAreaMarginPt,
        runCapSeconds = RunCapSeconds,
        killAfterSeconds = KillAfterSeconds,
        containerMemory = ContainerMemory,
        containerMemorySwap = ContainerMemorySwap,
        nearCeilingSeconds = NearCeilingSeconds,
        nearCeilingTreeKiB = NearCeilingTreeKiB,
        workFolderCeilingBytes = WorkFolderCeilingBytes,
        stageBudgetSeconds = StageBudgetSeconds,
        repetitions = Repetitions,
        configurations = Readers.Configurations,
        judgedTextRules = Judged.TextRules,
        judgedStrictRules = Judged.StrictRules,
    };
}

/// <summary>Reader identifiers and their configurations.</summary>
internal static class Readers
{
    public const string Oracle = "oracle";

    public const string PdfPig = "pdfpig";

    public const string PdfSharp = "pdfsharp";

    /// <summary>Oracle: poppler words as the earlier checker reads them.</summary>
    public const string OracleWords = "oracle";

    /// <summary>PdfPig, default word extractor, the library's own word boxes.</summary>
    public const string PdfPigAsShipped = "pdfpig-a";

    /// <summary>PdfPig, the same words, vertical extent from the baseline and the point size.</summary>
    public const string PdfPigBaseline = "pdfpig-b";

    public static readonly IReadOnlyDictionary<string, string[]> Configurations = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Oracle] = [OracleWords],
        [PdfPig] = [PdfPigAsShipped, PdfPigBaseline],
        [PdfSharp] = [],
    };

    public static readonly string[] CandidateConfigurations = [PdfPigAsShipped, PdfPigBaseline];
}

/// <summary>The rules whose verdicts are judged: the checks that need a reader.</summary>
internal static class Judged
{
    /// <summary>Row coverage (TI-2) and text completeness (TI-3, TI-4, TI-6).</summary>
    public static readonly string[] TextRules = ["TI-2", "TI-3", "TI-4", "TI-6"];

    /// <summary>Strict accounting rules 1 (rows), 2 (page subtotals) and 3 (the chain), named as the verifier names them.</summary>
    public static readonly int[] StrictRules = [1, 2, 3];

    public static string StrictName(int rule) => "S" + rule.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
