using PageAccountingCheck;
using PaginationFixtures;
using StrictAccountingCheck;
using TextIntegrityCheck;

namespace ReaderAgreement;

/// <summary>One judged rule's verdict: outcome, failure classes and first failing page.</summary>
internal sealed record RuleVerdict(string Rule, string Outcome, string[] Classes, int? Page);

/// <summary>Everything the unchanged rules say about one reading of one PDF.</summary>
internal sealed class ReadingVerdicts
{
    public string Pdf { get; set; } = string.Empty;

    public string Configuration { get; set; } = string.Empty;

    /// <summary>checked | no-check.</summary>
    public string Status { get; set; } = "checked";

    public string? Reason { get; set; }

    public int Pages { get; set; }

    public string TextVerdict { get; set; } = string.Empty;

    public string ContentSha256 { get; set; } = string.Empty;

    public int FailureCount { get; set; }

    public List<string> FailureClasses { get; set; } = [];

    /// <summary>TI-1 … TI-8 as the text rules report them (pass | fail).</summary>
    public SortedDictionary<string, string> TextRules { get; set; } = [];

    /// <summary>Strict rules 1-9 as the verifier reports them (accounting PDFs only).</summary>
    public List<RuleLine> StrictRules { get; set; } = [];

    public string? AccountingVerdict { get; set; }

    /// <summary>The judged verdicts: TI-2, TI-3, TI-4, TI-6, and S1-S3 when the PDF carries the accounting footer.</summary>
    public List<RuleVerdict> Judged { get; set; } = [];
}

/// <summary>
/// Runs the unchanged rules on any reader's words. It follows the order of the
/// strict check's per-run evaluation: the text rules first, then the strict
/// judgement fed with the text result, with no harness record (rules 7 and 8
/// are not evaluated). It never knows which reader produced the words.
/// </summary>
internal static class Verdicts
{
    public static ReadingVerdicts Evaluate(PdfEntry entry, string configuration, PdfText text, Fixture fixture)
    {
        var result = new ReadingVerdicts { Pdf = entry.Id, Configuration = configuration };
        try
        {
            var analysis = new IntegrityRules(fixture, entry.Marks, entry.Footer).Analyze(PrintedDocument.From(text));
            result.Pages = analysis.Pages;
            result.TextVerdict = analysis.Pass ? "pass" : "fail";
            result.ContentSha256 = analysis.ContentSha256;
            result.FailureCount = analysis.Failures.Count;
            result.FailureClasses = [.. analysis.Failures.Select(f => f.Class).Distinct().Order(StringComparer.Ordinal)];
            result.TextRules = analysis.Rules;
            foreach (var rule in Judged.TextRules)
            {
                var failures = analysis.Failures.Where(f => f.Rule == rule).ToList();
                var outcome = analysis.Rules.GetValueOrDefault(rule, "missing");
                result.Judged.Add(new RuleVerdict(
                    rule,
                    outcome,
                    [.. failures.Select(f => f.Class).Distinct().Order(StringComparer.Ordinal)],
                    failures.Where(f => f.Page is not null).Select(f => f.Page).Min()));
            }

            if (entry.Accounting)
            {
                var integrity = new IntegrityEntry
                {
                    RunId = entry.Id,
                    Verdict = result.TextVerdict,
                    Pages = analysis.Pages,
                    ContentSha256 = analysis.ContentSha256,
                    FailureCount = analysis.Failures.Count,
                    Failures = [.. analysis.Failures.Select(f => new IntegrityFailure { Rule = f.Rule, Class = f.Class, Row = f.Row, Region = f.Region, Field = f.Field, Page = f.Page, Detail = f.Detail })],
                    Rules = analysis.Rules,
                };
                var strict = new StrictEntry { Run = entry.Id, Markup = entry.Markup ?? string.Empty, FixtureId = fixture.Id, FooterOn = true };
                var reading = PdfReading.From(PrintedDocument.From(text), fixture, entry.Marks, entry.Footer);
                Account.Judge(strict, reading, fixture, null, integrity);
                result.StrictRules = strict.Rules;
                result.AccountingVerdict = strict.AccountingVerdict;
                foreach (var n in Judged.StrictRules)
                {
                    var line = strict.Rules.FirstOrDefault(r => r.Rule == n);
                    result.Judged.Add(new RuleVerdict(Judged.StrictName(n), line is null ? "missing" : Outcome(line.Outcome), [], line?.Outcome == "FAIL" ? line.Page : null));
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or FormatException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException or NullReferenceException)
        {
            result.Status = "no-check";
            result.Reason = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
        }

        return result;
    }

    /// <summary>The strict verifier's outcome in the text rules' words.</summary>
    public static string Outcome(string verifierOutcome) => verifierOutcome switch
    {
        "PASS" => "pass",
        "FAIL" => "fail",
        _ => verifierOutcome.ToLowerInvariant(),
    };

    /// <summary>Whether two judged verdicts agree: same outcome, and for a failure the same classes and first failing page.</summary>
    public static bool Same(RuleVerdict a, RuleVerdict b) =>
        a.Outcome == b.Outcome && (a.Outcome != "fail" || (a.Classes.SequenceEqual(b.Classes) && a.Page == b.Page));
}
