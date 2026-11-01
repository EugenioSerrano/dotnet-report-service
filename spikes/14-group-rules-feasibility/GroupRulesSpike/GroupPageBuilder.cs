using System.Text;
using PaginationFixtures;

namespace PreSplitLayoutSpike;

/// <summary>
/// The pages of one run for one configuration. Both pages come from the grid page
/// builder of spikes/08-pagedjs-pre-split, unchanged. For a configuration with group
/// behaviours, only the PDF page's head gains elements, before its end tag: the
/// group-container stylesheet (when the repeated header is attempted) and one script
/// element that names the attempted rules in its data-rules attribute. The body and
/// the semantic page are never changed; every build checks that the PDF page's body
/// still equals the semantic page's body.
/// </summary>
internal sealed class GroupPageBuilder
{
    private readonly PageBuilder inner;

    public GroupPageBuilder(PageBuilder inner, string configuration, string groupRulesCss)
    {
        this.inner = inner;
        Rules = Plans.Rules(configuration);
        var head = new StringBuilder();
        if (Rules.Contains(Plans.RepeatHeader))
        {
            head.Append("<style>\n").Append(groupRulesCss).Append("\n</style>\n");
        }

        if (Rules.Count > 0)
        {
            head.Append("<script src=\"assets/group-rules.js\" data-rules=\"").Append(string.Join(' ', Rules)).Append("\"></script>\n");
        }

        HeadInsertion = head.ToString();
    }

    /// <summary>The group rules the PDF page attempts (none for the configuration without behaviours).</summary>
    public IReadOnlyList<string> Rules { get; }

    /// <summary>The elements inserted before the PDF page's head end tag (empty without behaviours).</summary>
    public string HeadInsertion { get; }

    /// <summary>The script elements the PDF page holds: the grid builder's four, plus the group-rules script when it is added.</summary>
    public int PdfScripts => PageBuilder.PdfScripts + (Rules.Count > 0 ? 1 : 0);

    public (string Semantic, string Pdf) Build(Fixture fixture, bool footerOn)
    {
        var (semantic, pdf) = inner.Build(fixture, footerOn);
        if (HeadInsertion.Length == 0)
        {
            return (semantic, pdf);
        }

        var at = pdf.IndexOf("</head>", StringComparison.Ordinal);
        if (at < 0)
        {
            throw new InvalidOperationException("The PDF page has no head end tag.");
        }

        var withRules = pdf.Insert(at, HeadInsertion);
        var semanticBody = PageBuilder.BodyInner(Encoding.UTF8.GetBytes(semantic));
        var pdfBody = PageBuilder.BodyInner(Encoding.UTF8.GetBytes(withRules));
        if (semanticBody is null || pdfBody is null || !semanticBody.Value.Inner.AsSpan().SequenceEqual(pdfBody.Value.Inner))
        {
            throw new InvalidOperationException("The PDF page's body differs from the semantic page's body after the head insertion.");
        }

        return (semantic, withRules);
    }
}
