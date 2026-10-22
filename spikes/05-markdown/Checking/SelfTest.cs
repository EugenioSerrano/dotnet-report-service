using System.Text;
using MarkdownSpike.Rendering;
using MarkdownSpike.Routes;

namespace MarkdownSpike.Checking;

/// <summary>Assertion checks of the spike's own code. Exit code 0 only when every check passes.</summary>
internal static class SelfTest
{
    private const string SplitTableA = "<table><thead><tr><th>#</th><th>Name</th></tr></thead><tbody><tr><td>1</td><td>alpha</td></tr>";
    private const string SplitTableB = "<tr><td>2</td><td>beta</td></tr><tr><td>3</td>";
    private const string SplitTableC = "<td>gamma</td></tr></tbody></table>";

    public static int Run(CommandLine line)
    {
        line.Allow();
        var checks = new List<(string Name, Func<string?> Check)>
        {
            ("tokenizer: '>' inside a quoted attribute value", TokenizerQuotedGreaterThan),
            ("tokenizer: unquoted and empty attributes", TokenizerUnquotedAndEmpty),
            ("tokenizer: upper-case tags", TokenizerUpperCase),
            ("tokenizer: self-closing <br/>", TokenizerSelfClosing),
            ("tokenizer: comment and document type skipped", TokenizerCommentAndDoctype),
            ("tokenizer: entities decoded (&amp; &lt; &#233; &eacute;)", TokenizerEntities),
            ("tokenizer: a tag split across two fragments", TokenizerSplitTag),
            ("tokenizer: a surrogate pair and an entity split across fragments", TokenizerSplitCharacter),
            ("escaping: Escape/Unescape round trip on hostile and special text", EscapeRoundTrip),
            ("route B: a table split into three fragments", RouteBSplitTable),
            ("route A: the same table split at row boundaries gives the same table lines as route B", RouteAMatchesRouteB),
            ("checker: correct output is continuous", () => CheckerOutcome(CorrectOutput, "continuous")),
            ("checker: a repeated header is broken", () => CheckerOutcome(CorrectOutput.Replace("| 2 | beta |", "| # | Name |\n| 2 | beta |", StringComparison.Ordinal), "broken")),
            ("checker: a missing row is broken", () => CheckerOutcome(CorrectOutput.Replace("| 2 | beta |\n", string.Empty, StringComparison.Ordinal), "broken")),
            ("checker: a second table is broken", () => CheckerOutcome(CorrectOutput.Replace("| 2 | beta |", "\n| # | Name |\n| --- | --- |\n| 2 | beta |", StringComparison.Ordinal), "broken")),
        };

        int failures = 0;
        foreach ((string name, Func<string?> check) in checks)
        {
            string? problem;
            try
            {
                problem = check();
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or FormatException or ArgumentException)
            {
                problem = $"{ex.GetType().Name}: {ex.Message}";
            }

            Console.WriteLine(problem is null ? $"PASS  {name}" : $"FAIL  {name}: {problem}");
            failures += problem is null ? 0 : 1;
        }

        Console.WriteLine(failures == 0 ? $"selftest: all {checks.Count} checks PASS" : $"selftest: {failures} of {checks.Count} checks FAIL");
        return failures == 0 ? 0 : 1;
    }

    private const string CorrectOutput = "# Self test\n\nCustomer\n\n| # | Name |\n| --- | --- |\n| 1 | alpha |\n| 2 | beta |\n| 3 | gamma |\n\nTotal: 6\n";

    private static List<HtmlToken> Tokens(params string[] fragments)
    {
        var tokens = new List<HtmlToken>();
        var tokenizer = new HtmlTokenizer();
        foreach (string fragment in fragments)
        {
            tokenizer.Feed(fragment, tokens.Add);
        }

        tokenizer.Finish(tokens.Add);
        return tokens;
    }

    private static string? Expect(bool condition, string problem) => condition ? null : problem;

    private static string? TokenizerQuotedGreaterThan()
    {
        List<HtmlToken> tokens = Tokens("<a title=\"a > b\" href='x>y'>t</a>");
        return Expect(tokens.Count == 3 && tokens[0].Attribute("title") == "a > b" && tokens[0].Attribute("href") == "x>y" && tokens[1].Text == "t", Describe(tokens));
    }

    private static string? TokenizerUnquotedAndEmpty()
    {
        List<HtmlToken> tokens = Tokens("<td colspan=5 hidden class=\"\">x</td>");
        return Expect(tokens[0].Attribute("colspan") == "5" && tokens[0].Attribute("hidden") == string.Empty && tokens[0].Attribute("class") == string.Empty, Describe(tokens));
    }

    private static string? TokenizerUpperCase()
    {
        List<HtmlToken> tokens = Tokens("<TABLE><TR><TD>x</TD></TR></TABLE>");
        return Expect(tokens[0].Name == "table" && tokens[1].Name == "tr" && tokens[4].Kind == HtmlTokenKind.EndTag && tokens[4].Name == "td", Describe(tokens));
    }

    private static string? TokenizerSelfClosing()
    {
        List<HtmlToken> tokens = Tokens("a<br/>b<br />c");
        return Expect(tokens.Count == 5 && tokens[1].Name == "br" && tokens[1].SelfClosing && tokens[3].SelfClosing, Describe(tokens));
    }

    private static string? TokenizerCommentAndDoctype()
    {
        var tokenizer = new HtmlTokenizer();
        var tokens = new List<HtmlToken>();
        tokenizer.Feed("<!DOCTYPE html><!-- a <b> -- comment --><p>x</p>", tokens.Add);
        tokenizer.Finish(tokens.Add);
        return Expect(tokens.Count == 3 && tokens[0].Name == "p" && tokenizer.Skipped["comment"] == 1 && tokenizer.Skipped["declaration"] == 1, Describe(tokens));
    }

    private static string? TokenizerEntities()
    {
        List<HtmlToken> tokens = Tokens("<p>&amp; &lt; &#233; &eacute;</p>");
        return Expect(tokens[1].Text == "& < é é", Describe(tokens));
    }

    private static string? TokenizerSplitTag()
    {
        List<HtmlToken> tokens = Tokens("<p>a</p><td cl", "ass=\"x\">b</td>");
        return Expect(tokens.Count == 6 && tokens[3].Name == "td" && tokens[3].Attribute("class") == "x" && tokens[4].Text == "b", Describe(tokens));
    }

    private static string? TokenizerSplitCharacter()
    {
        const string emoji = "\U0001F600";
        List<HtmlToken> tokens = Tokens("<p>x" + emoji[0], emoji[1] + " caf&eac", "ute;</p>");
        return Expect(tokens.Count == 3 && tokens[1].Text == "x" + emoji + " café", Describe(tokens));
    }

    private static string? EscapeRoundTrip()
    {
        string[] samples =
        [
            "<script>alert(1)</script>",
            "Tom & Jerry \"quoted\" <b>bold</b>",
            "'; DROP TABLE items; --",
            "{{ header.customer.name }}",
            "{% raw %}{{ 7*7 }}{% endraw %}",
            "${7*7}",
            "| * _ [ ] \\ `",
            "reparación tensión ñ €",
            "&amp; already an entity &eacute;",
        ];
        foreach (string sample in samples)
        {
            string escaped = MarkdownText.Escape(sample);
            string back = MarkdownText.Unescape(escaped);
            if (back != sample)
            {
                return $"'{sample}' -> '{escaped}' -> '{back}'";
            }

            if (escaped.Contains('<', StringComparison.Ordinal) || ContinuityChecker.CountCells("| " + escaped + " |") != 1)
            {
                return $"'{escaped}' still carries a tag opener or an unescaped pipe";
            }
        }

        return Expect(MarkdownText.Escape("a\r\n\tb") == "a b", "line breaks and tabs are not collapsed");
    }

    private static List<string> TableLines(string markdown) =>
        [.. markdown.Split('\n').Where(l => l.StartsWith('|')).Select(l => string.Join("|", l.Split('|').Select(c => c.Trim())))];

    private static string RouteB(params string[] fragments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var converter = new StreamingMarkdownConverter(output);
        foreach (string fragment in fragments)
        {
            converter.OnFragment(fragment, FragmentKind.Block);
        }

        converter.Complete();
        return output.ToString();
    }

    private static string? RouteBSplitTable()
    {
        string markdown = RouteB(SplitTableA, SplitTableB, SplitTableC);
        List<string> lines = TableLines(markdown);
        string[] expected = ["|#|Name|", "|---|---|", "|1|alpha|", "|2|beta|", "|3|gamma|"];
        return Expect(lines.SequenceEqual(expected) && markdown.Split('\n').Count(l => l.StartsWith('|')) == 5, markdown);
    }

    private static string? RouteAMatchesRouteB()
    {
        var output = new StringWriter { NewLine = "\n" };
        var converter = new FragmentStitchingConverter(output);
        converter.OnFragment("<html><body>" + SplitTableA, FragmentKind.Block);
        converter.OnFragment(SplitTableB.Replace("<tr><td>3</td>", string.Empty, StringComparison.Ordinal), FragmentKind.Block);
        converter.OnFragment("<tr><td>3</td>" + SplitTableC + "</body></html>", FragmentKind.Block);
        converter.Complete();
        List<string> a = TableLines(output.ToString());
        List<string> b = TableLines(RouteB(SplitTableA, SplitTableB, SplitTableC));
        return Expect(a.SequenceEqual(b), "route A:\n" + output + "\nroute B lines:\n" + string.Join("\n", b));
    }

    private static string? CheckerOutcome(string markdown, string expectedOutcome)
    {
        string folder = Path.Combine(Path.GetTempPath(), "markdown-spike-selftest-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        string data = Path.Combine(folder, "data.json");
        File.WriteAllText(data, """
            {"contractVersion":"1.0","header":{"datasetId":"selftest","reportTitle":"Self test","customer":{"id":"C1","name":"Customer"},"totals":{"recordCount":3,"amount":6}},
             "items":[{"sequence":1,"description":"alpha","amount":1},{"sequence":2,"description":"beta","amount":2},{"sequence":3,"description":"gamma","amount":3}]}
            """, new UTF8Encoding(false));
        try
        {
            CheckResult result = ContinuityChecker.Check(data, new StringReader(markdown), 1, 0, 0, "per-document");
            return Expect(result.Outcome == expectedOutcome && (expectedOutcome == "broken" || result.HeaderFieldsFound == 3), $"outcome {result.Outcome}, header fields {result.HeaderFieldsFound}, failures: {string.Join("; ", result.Failures)}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Describe(List<HtmlToken> tokens)
    {
        var text = new StringBuilder();
        foreach (HtmlToken token in tokens)
        {
            text.Append(token.Kind).Append(':').Append(token.Kind == HtmlTokenKind.Text ? token.Text : token.Name).Append(' ');
        }

        return text.ToString();
    }
}
