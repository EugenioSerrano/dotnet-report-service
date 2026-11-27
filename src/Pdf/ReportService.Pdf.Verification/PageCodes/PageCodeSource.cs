using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReportService.Pdf.Verification.PageCodes;

/// <summary>What one page offers the value source.</summary>
/// <param name="PageText">The page's own text, outside the numbering and code areas (<see cref="PageCodeText"/>).</param>
/// <param name="PageNumber">The page's number: its global position, or its index in a one-part document.</param>
/// <param name="PageTotal">The document's page count.</param>
/// <param name="TemplateId">The taken template's identifier.</param>
/// <param name="TemplateVersion">The taken template's version.</param>
public sealed record PageCodeInputs(string PageText, int PageNumber, int PageTotal, string TemplateId, string TemplateVersion);

/// <summary>One problem the declaration's source has, with its fix.</summary>
/// <param name="Field">The manifest field.</param>
/// <param name="Problem">What is wrong.</param>
/// <param name="Fix">How to correct it.</param>
public sealed record PageCodeProblem(string Field, string Problem, string Fix);

/// <summary>
/// A per-page code's value source: a template of literals and closed tokens, and
/// at most one bounded match over the page's own text. It is data evaluated by the
/// product, per page; it calls nothing and has no effect.
/// </summary>
public sealed class PageCodeSource
{
    /// <summary>The largest value, in UTF-8 bytes.</summary>
    public const int MaximumValueBytes = 128;

    /// <summary>The bytes a page token counts in the static bound: seven digits.</summary>
    public const int PageTokenBytes = 7;

    /// <summary>The largest page number or total the bound admits.</summary>
    public const int MaximumPageNumber = 9_999_999;

    /// <summary>The bytes one matched character counts in the static bound.</summary>
    public const int MatchCharacterBytes = 4;

    /// <summary>The longest value template, in characters.</summary>
    public const int MaximumTemplateLength = 256;

    /// <summary>The longest pattern, in characters.</summary>
    public const int MaximumPatternLength = 256;

    /// <summary>The largest declared match length.</summary>
    public const int MaximumMatchLength = 128;

    /// <summary>How long one page's match may run.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private const string PageNumberToken = "page.number";
    private const string PageTotalToken = "page.total";
    private const string TemplateIdToken = "template.id";
    private const string TemplateVersionToken = "template.version";
    private const string MatchToken = "match";

    private static readonly HashSet<string> Tokens = new(StringComparer.Ordinal)
    {
        PageNumberToken, PageTotalToken, TemplateIdToken, TemplateVersionToken, MatchToken,
    };

    private readonly IReadOnlyList<Segment> _segments;
    private readonly Regex? _match;

    private PageCodeSource(IReadOnlyList<Segment> segments, Regex? match, int? maxLength)
    {
        _segments = segments;
        _match = match;
        MatchLength = maxLength;
    }

    /// <summary>The declared longest match, when the source uses <c>{match}</c>.</summary>
    public int? MatchLength { get; }

    /// <summary>Whether the source reads the page's text.</summary>
    public bool UsesMatch => _match is not null;

    /// <summary>Parses a declared source.</summary>
    /// <param name="value">The value template.</param>
    /// <param name="pattern">The match pattern, when declared.</param>
    /// <param name="maxLength">The match's declared longest length, when declared.</param>
    /// <param name="problems">Where problems are added.</param>
    /// <returns>The source, or <see langword="null"/> when it has problems.</returns>
    public static PageCodeSource? Parse(string? value, string? pattern, int? maxLength, ICollection<PageCodeProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var before = problems.Count;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumTemplateLength)
        {
            problems.Add(new PageCodeProblem(
                "source.value",
                "the value template is empty or longer than " + MaximumTemplateLength + " characters",
                "write a value template of 1 to " + MaximumTemplateLength + " characters"));
            return null;
        }

        var segments = new List<Segment>();
        var literal = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '{' && i + 1 < value.Length && value[i + 1] == '{')
            {
                literal.Append('{');
                i++;
                continue;
            }

            if (c == '}' && i + 1 < value.Length && value[i + 1] == '}')
            {
                literal.Append('}');
                i++;
                continue;
            }

            if (c == '}')
            {
                problems.Add(new PageCodeProblem("source.value", "the value template has a '}' that closes no token", "write '}}' for a literal brace"));
                return null;
            }

            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            var end = value.IndexOf('}', i + 1);
            if (end < 0)
            {
                problems.Add(new PageCodeProblem("source.value", "the value template has a '{' that opens no token", "close the token, or write '{{' for a literal brace"));
                return null;
            }

            var token = value[(i + 1)..end];
            if (!Tokens.Contains(token))
            {
                problems.Add(new PageCodeProblem(
                    "source.value",
                    "the value template uses an unknown token {" + token + "}",
                    "use only {page.number}, {page.total}, {template.id}, {template.version} and {match}"));
                return null;
            }

            if (literal.Length > 0)
            {
                segments.Add(new Segment(literal.ToString(), null));
                literal.Clear();
            }

            segments.Add(new Segment(null, token));
            i = end;
        }

        if (literal.Length > 0)
        {
            segments.Add(new Segment(literal.ToString(), null));
        }

        var usesMatch = segments.Any(segment => segment.Token == MatchToken);
        Regex? regex = null;
        if (usesMatch)
        {
            if (string.IsNullOrEmpty(pattern) || pattern.Length > MaximumPatternLength)
            {
                problems.Add(new PageCodeProblem(
                    "source.match.pattern",
                    "the value template uses {match} but no pattern of 1 to " + MaximumPatternLength + " characters is declared",
                    "declare source.match.pattern"));
            }
            else
            {
                try
                {
                    regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);
                }
                catch (ArgumentException)
                {
                    problems.Add(new PageCodeProblem("source.match.pattern", "the match pattern is not a valid pattern", "correct the pattern"));
                }
                catch (NotSupportedException)
                {
                    problems.Add(new PageCodeProblem(
                        "source.match.pattern",
                        "the match pattern uses a construct the linear-time matcher does not support",
                        "remove back-references and look-arounds from the pattern"));
                }
            }

            if (maxLength is not { } length || length < 1 || length > MaximumMatchLength)
            {
                problems.Add(new PageCodeProblem(
                    "source.match.maxLength",
                    "the match's maxLength is missing or outside 1 to " + MaximumMatchLength,
                    "declare source.match.maxLength as the real width of the matched field"));
            }
        }
        else if (pattern is not null || maxLength is not null)
        {
            problems.Add(new PageCodeProblem("source.match", "a match is declared but the value template does not use {match}", "use {match} in the value, or remove source.match"));
        }

        return problems.Count == before ? new PageCodeSource(segments, regex, usesMatch ? maxLength : null) : null;
    }

    /// <summary>The static upper bound of the value's UTF-8 bytes for a template identity.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="templateVersion">The template's version.</param>
    /// <returns>The bound.</returns>
    public int StaticBound(string templateId, string templateVersion) =>
        BoundWithout(templateId, templateVersion) + (UsesMatch ? MatchCharacterBytes * (MatchLength ?? 0) * Count(MatchToken) : 0);

    /// <summary>The largest <c>maxLength</c> the bound admits for this template identity.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="templateVersion">The template's version.</param>
    /// <returns>The largest admissible match length, never negative.</returns>
    public int LargestMatchLength(string templateId, string templateVersion)
    {
        var uses = Math.Max(1, Count(MatchToken));
        return Math.Max(0, (MaximumValueBytes - BoundWithout(templateId, templateVersion)) / (MatchCharacterBytes * uses));
    }

    /// <summary>Evaluates the source on one page.</summary>
    /// <param name="inputs">What the page offers.</param>
    /// <returns>The value, or <see langword="null"/> when the source yields no value on this page.</returns>
    public string? Evaluate(PageCodeInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.PageNumber > MaximumPageNumber || inputs.PageTotal > MaximumPageNumber)
        {
            return null;
        }

        string? matched = null;
        if (_match is not null)
        {
            Match found;
            try
            {
                found = _match.Match(inputs.PageText);
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }

            if (!found.Success)
            {
                return null;
            }

            var group = found.Groups["value"];
            matched = group.Success ? group.Value : found.Value;
            if (matched.Length == 0 || matched.Length > MatchLength)
            {
                return null;
            }
        }

        var builder = new StringBuilder();
        foreach (var segment in _segments)
        {
            builder.Append(segment.Token switch
            {
                null => segment.Literal,
                PageNumberToken => inputs.PageNumber.ToString(CultureInfo.InvariantCulture),
                PageTotalToken => inputs.PageTotal.ToString(CultureInfo.InvariantCulture),
                TemplateIdToken => inputs.TemplateId,
                TemplateVersionToken => inputs.TemplateVersion,
                _ => matched,
            });
        }

        var result = builder.ToString();
        return Encoding.UTF8.GetByteCount(result) > MaximumValueBytes ? null : result;
    }

    private int BoundWithout(string templateId, string templateVersion)
    {
        var bytes = 0;
        foreach (var segment in _segments)
        {
            bytes += segment.Token switch
            {
                null => Encoding.UTF8.GetByteCount(segment.Literal!),
                PageNumberToken or PageTotalToken => PageTokenBytes,
                TemplateIdToken => Encoding.UTF8.GetByteCount(templateId),
                TemplateVersionToken => Encoding.UTF8.GetByteCount(templateVersion),
                _ => 0,
            };
        }

        return bytes;
    }

    private int Count(string token) => _segments.Count(segment => segment.Token == token);

    private sealed record Segment(string? Literal, string? Token);
}

/// <summary>
/// The page text the value source reads: the page's words whose centre lies outside
/// the excluded areas, joined by one space in reading order, whitespace collapsed,
/// normalised to NFC. The assembler reads it on the source page and the verifier on
/// the assembled page; one function serves both.
/// </summary>
public static class PageCodeText
{
    /// <summary>A rectangle in page points, origin at the page's top-left corner.</summary>
    /// <param name="LeftPt">The left edge.</param>
    /// <param name="TopPt">The top edge.</param>
    /// <param name="WidthPt">The width.</param>
    /// <param name="HeightPt">The height.</param>
    public sealed record Region(double LeftPt, double TopPt, double WidthPt, double HeightPt)
    {
        /// <summary>A region declared in millimetres from the page's top-left corner.</summary>
        /// <param name="leftMm">The left edge.</param>
        /// <param name="topMm">The top edge.</param>
        /// <param name="widthMm">The width.</param>
        /// <param name="heightMm">The height.</param>
        /// <returns>The region in points.</returns>
        public static Region FromMillimetres(double leftMm, double topMm, double widthMm, double heightMm)
        {
            const double points = 72d / 25.4d;
            return new Region(leftMm * points, topMm * points, widthMm * points, heightMm * points);
        }

        /// <summary>Whether this region lies inside another (with a small tolerance).</summary>
        /// <param name="outer">The enclosing region.</param>
        /// <returns>Whether it is inside.</returns>
        public bool Within(Region outer)
        {
            ArgumentNullException.ThrowIfNull(outer);
            const double tolerance = 0.05d;
            return LeftPt >= outer.LeftPt - tolerance && TopPt >= outer.TopPt - tolerance
                && LeftPt + WidthPt <= outer.LeftPt + outer.WidthPt + tolerance
                && TopPt + HeightPt <= outer.TopPt + outer.HeightPt + tolerance;
        }

        /// <summary>Whether a word's centre lies inside the region (with a small tolerance).</summary>
        /// <param name="word">The word.</param>
        /// <returns>Whether it is inside.</returns>
        public bool Contains(PrintedWord word)
        {
            ArgumentNullException.ThrowIfNull(word);
            const double tolerance = 0.05d;
            var x = (word.Left + word.Right) / 2d;
            var y = (word.Top + word.Bottom) / 2d;
            return x >= LeftPt - tolerance && x <= LeftPt + WidthPt + tolerance
                && y >= TopPt - tolerance && y <= TopPt + HeightPt + tolerance;
        }

        /// <summary>Whether two regions overlap.</summary>
        /// <param name="other">The other region.</param>
        /// <returns>Whether they overlap.</returns>
        public bool Overlaps(Region other)
        {
            ArgumentNullException.ThrowIfNull(other);
            return LeftPt < other.LeftPt + other.WidthPt && other.LeftPt < LeftPt + WidthPt
                && TopPt < other.TopPt + other.HeightPt && other.TopPt < TopPt + HeightPt;
        }
    }

    /// <summary>The page text outside the excluded regions.</summary>
    /// <param name="page">The page.</param>
    /// <param name="excluded">The regions whose words are left out.</param>
    /// <returns>The text.</returns>
    public static string Of(PrintedPage page, params Region?[] excluded)
    {
        ArgumentNullException.ThrowIfNull(page);
        var words = page.Words
            .Where(word => !excluded.Any(region => region is not null && region.Contains(word)))
            .Select(word => word.Text);
        var joined = string.Join(' ', string.Join(' ', words).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return joined.Normalize(NormalizationForm.FormC);
    }

    /// <summary>The words whose centre lies inside a region, in reading order.</summary>
    /// <param name="page">The page.</param>
    /// <param name="region">The region.</param>
    /// <returns>The words' texts.</returns>
    public static IReadOnlyList<string> Inside(PrintedPage page, Region region)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(region);
        return [.. page.Words.Where(region.Contains).Select(word => word.Text)];
    }
}
