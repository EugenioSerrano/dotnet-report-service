using System.Text;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>
/// A version that prints page subtotals is laid out on the accounting path, and that
/// path admits one shape of detail: grid items, one root element per row, with the
/// amount cell and the group-total cell marked by a literal role attribute. This
/// check reads the package's own text for the defects the path cannot work around:
/// table markup for detail, a row part that emits more than one root element, and a
/// missing role marker. A construct the literal reader cannot place — a role value a
/// template expression supplies, a part it cannot read — is recorded as an
/// observation and never refuses, because the check refuses only a demonstrated
/// defect. A version that declares no page totals is unaffected.
/// </summary>
public sealed class AccountedDetailMarkupCheck(TemplateCheckSession session) : ITemplateVersionCheck
{
    /// <summary>The rule identifier this refusal is counted under.</summary>
    public const string RuleId = TemplateRefusal.Rules.AccountedDetail;

    /// <summary>The literal role the amount cell of a row carries.</summary>
    public const string AmountRole = "amount";

    /// <summary>The literal role the group-total cell of a group end carries.</summary>
    public const string GroupTotalRole = "group-total";

    private readonly TemplateCheckSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <inheritdoc />
    public string Name => "AccountedDetailMarkupCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = candidate.Manifest;
        var pdf = manifest.Outputs?.Pdf;
        if (pdf?.PageTotals is not { Enabled: true })
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var parts = pdf.Parts ?? manifest.Outputs?.Html?.Parts;
        var row = parts?.Row;
        if (string.IsNullOrEmpty(row))
        {
            // The detail text is not in a part this version declares; the structural
            // rules own that defect, and this rule refuses nothing it cannot read.
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var view = _session.View(candidate);
        if (!view.Exists(row))
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        string text;
        try
        {
            text = view.ReadText(row);
        }
        catch (DecoderFallbackException)
        {
            // A part that is not text is refused by the syntax check, before this rule.
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var scan = MarkupScan.Of(text);
        if (scan.TableElement is { } table)
        {
            return Task.FromResult(Refuse(
                row,
                "the row part lays the detail out with table markup (<" + table + ">).",
                "Lay the detail out as grid items, one root element per row."));
        }

        if (scan.Roots != 1)
        {
            return Task.FromResult(Refuse(
                row,
                "the row part emits " + TemplateRefusal.Number(scan.Roots) + " root elements where the accounting path measures and cuts exactly one.",
                "Wrap the row in one grid item that the split step can measure."));
        }

        if (!scan.HasRole(AmountRole))
        {
            return Task.FromResult(Refuse(
                row,
                "the row part carries no literal data-role=\"amount\" cell.",
                "Mark the cell the record's amount prints in with data-role=\"amount\"."));
        }

        var groupKey = manifest.Partitioning?.GroupKey;
        if (!string.IsNullOrEmpty(groupKey))
        {
            var groupEnd = parts?.GroupEnd;
            if (string.IsNullOrEmpty(groupEnd))
            {
                return Task.FromResult(Refuse(
                    row,
                    "the version groups the records but declares no group-end part to print a group total in.",
                    "Declare the group-end part and mark its total cell with data-role=\"group-total\"."));
            }

            if (view.Exists(groupEnd))
            {
                string groupText;
                try
                {
                    groupText = view.ReadText(groupEnd);
                }
                catch (DecoderFallbackException)
                {
                    return Task.FromResult(TemplateCheckResult.Pass());
                }

                var groupScan = MarkupScan.Of(groupText);
                if (groupScan.TableElement is { } tableElement)
                {
                    return Task.FromResult(Refuse(
                        groupEnd,
                        "the group-end part lays the detail out with table markup (<" + tableElement + ">).",
                        "Lay the detail out as grid items, one root element per row."));
                }

                if (!groupScan.HasRole(GroupTotalRole))
                {
                    return Task.FromResult(Refuse(
                        groupEnd,
                        "the group-end part carries no literal data-role=\"group-total\" cell.",
                        "Mark the cell the group's total prints in with data-role=\"group-total\"."));
                }
            }
        }

        return Task.FromResult(TemplateCheckResult.Pass());
    }

    private static TemplateCheckResult Refuse(string file, string problem, string fix) =>
        TemplateRefusal.Refuse(TemplateCheckCodes.TemplateInvalid, file, problem, fix);

    /// <summary>
    /// What a part's own text demonstrates: the first table element it opens, how many
    /// root elements it emits, and which literal roles its attributes carry. A role
    /// whose value is supplied by a template expression is not a demonstrated absence
    /// and is recorded as unplaceable instead.
    /// </summary>
    private sealed record MarkupScan(string? TableElement, int Roots, IReadOnlySet<string> Roles, bool HasDynamicRole)
    {
        public bool HasRole(string role) => Roles.Contains(role) || HasDynamicRole;

        public static MarkupScan Of(string text)
        {
            string? table = null;
            var roots = 0;
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dynamicRole = false;
            var depth = 0;

            var position = 0;
            while (position < text.Length)
            {
                var open = text.IndexOf('<', position);
                if (open < 0)
                {
                    break;
                }

                if (string.CompareOrdinal(text, open, "<!--", 0, 4) == 0)
                {
                    var end = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
                    position = end < 0 ? text.Length : end + 3;
                    continue;
                }

                if (string.CompareOrdinal(text, open, "<!", 0, 2) == 0 || string.CompareOrdinal(text, open, "<?", 0, 2) == 0)
                {
                    var end = text.IndexOf('>', open);
                    position = end < 0 ? text.Length : end + 1;
                    continue;
                }

                var tag = ReadTag(text, open);
                if (tag is null)
                {
                    position = open + 1;
                    continue;
                }

                if (tag.IsEnd)
                {
                    depth = Math.Max(0, depth - 1);
                }
                else
                {
                    if (table is null && PdfConstraintRules.TableElements.Contains(tag.Name))
                    {
                        table = tag.Name.ToLowerInvariant();
                    }

                    if (depth == 0 && !PdfConstraintRules.NonRenderedElements.Contains(tag.Name))
                    {
                        roots++;
                    }

                    foreach (var attribute in tag.Attributes)
                    {
                        if (!string.Equals(attribute.Name, "data-role", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var value = attribute.Value;
                        if (value.StartsWith("{{", StringComparison.Ordinal)
                            || value.StartsWith("{%", StringComparison.Ordinal)
                            || value.Contains("{{", StringComparison.Ordinal)
                            || value.Contains("{%", StringComparison.Ordinal))
                        {
                            dynamicRole = true;
                        }
                        else
                        {
                            _ = roles.Add(value);
                        }
                    }

                    if (!tag.SelfClosing && !PdfConstraintRules.NonRenderedElements.Contains(tag.Name))
                    {
                        depth++;
                    }
                }

                position = tag.End;
            }

            return new MarkupScan(table, roots, roles, dynamicRole);
        }

        private static Tag? ReadTag(string text, int open)
        {
            var position = open + 1;
            var isEnd = position < text.Length && text[position] == '/';
            if (isEnd)
            {
                position++;
            }

            var nameStart = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('>' or '/'))
            {
                position++;
            }

            var name = text[nameStart..position];
            if (name.Length == 0 || !char.IsLetter(name[0]))
            {
                return null;
            }

            var attributes = new List<TagAttribute>();
            while (position < text.Length)
            {
                while (position < text.Length && char.IsWhiteSpace(text[position]))
                {
                    position++;
                }

                if (position >= text.Length)
                {
                    break;
                }

                if (text[position] == '>')
                {
                    return new Tag(name, isEnd, position > 0 && text[position - 1] == '/', attributes, position + 1);
                }

                var attributeStart = position;
                while (position < text.Length
                    && !char.IsWhiteSpace(text[position])
                    && text[position] is not ('=' or '>' or '/' or '"' or '\''))
                {
                    position++;
                }

                var attributeName = text[attributeStart..position];
                var value = string.Empty;
                while (position < text.Length && char.IsWhiteSpace(text[position]))
                {
                    position++;
                }

                if (position < text.Length && text[position] == '=')
                {
                    position++;
                    while (position < text.Length && char.IsWhiteSpace(text[position]))
                    {
                        position++;
                    }

                    if (position < text.Length && text[position] is '"' or '\'')
                    {
                        var quote = text[position++];
                        var valueStart = position;
                        while (position < text.Length && text[position] != quote)
                        {
                            position++;
                        }

                        value = text[valueStart..position];
                        if (position < text.Length)
                        {
                            position++;
                        }
                    }
                    else
                    {
                        var valueStart = position;
                        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '>')
                        {
                            position++;
                        }

                        value = text[valueStart..position];
                    }
                }

                if (attributeName.Length > 0)
                {
                    attributes.Add(new TagAttribute(attributeName, value));
                }
                else
                {
                    position++;
                }
            }

            return new Tag(name, isEnd, false, attributes, text.Length);
        }

        private sealed record Tag(string Name, bool IsEnd, bool SelfClosing, IReadOnlyList<TagAttribute> Attributes, int End);

        private sealed record TagAttribute(string Name, string Value);
    }
}
