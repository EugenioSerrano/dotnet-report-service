using System.Text;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>
/// A PDF version whose detail lays a row out as a grid and bottom-aligns a cell that
/// may split across pages is refused: the split row's amount is then printed at the
/// foot of a later page, which is the misplaced total and the blank space this rule
/// exists to prevent. The rule refuses only a demonstrated defect — a grid container
/// in the row part, one of its direct children aligned by a declared Box Alignment
/// end value, and no declaration that forbids the break — and it names the part and
/// the cell. A construct the model cannot place is recorded as an observation and
/// never refuses, and neither a vertical alignment (which grid layout does not read)
/// nor a baseline value (which no measurement puts at the bottom) is a defect.
/// </summary>
public sealed class BottomAlignedGridCellCheck : ITemplateVersionCheck
{
    /// <summary>The rule identifier this refusal is counted under, carried to the take's log.</summary>
    public const string RuleId = TemplateRefusal.Rules.GridCellAlignment;

    private static readonly IReadOnlySet<string> DisplayProperties = Set(PdfConstraintRules.DisplayProperty);

    private static readonly IReadOnlySet<string> ItemAlignmentProperties = Set("align-self", "place-self");

    private static readonly IReadOnlySet<string> ContainerAlignmentProperties = Set("align-items", "place-items");

    private static readonly IReadOnlySet<string> Properties = Set(
        PdfConstraintRules.DisplayProperty,
        "align-items",
        "align-self",
        "place-items",
        "place-self",
        "break-inside",
        "page-break-inside");

    private readonly TemplateCheckSession _session;

    /// <summary>Creates the check over the take's shared session.</summary>
    /// <param name="session">The session the candidate's package is read through.</param>
    public BottomAlignedGridCellCheck(TemplateCheckSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <inheritdoc />
    public string Name => "BottomAlignedGridCellCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = candidate.Manifest;
        var pdf = manifest.Outputs?.Pdf;
        if (pdf is null)
        {
            // The grid of the detail text is a PDF constraint: a version that prints no
            // document is not asked for one.
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var parts = pdf.Parts ?? manifest.Outputs?.Html?.Parts;
        var row = parts?.Row;
        if (string.IsNullOrEmpty(row))
        {
            // The detail text is not in a part this version declares; refusing would
            // refuse a version whose text is printed somewhere the rule cannot see.
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var view = _session.View(candidate);
        if (!view.Exists(row))
        {
            // The structural rules refuse a part the package does not hold, before this rule.
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

        var elements = DetailMarkup.Read(row, text);
        if (elements.Count == 0)
        {
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var rules = Collect(candidate, view, parts, elements);
        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index].Parent is not { } container || !IsGrid(rules, elements, container))
            {
                continue;
            }

            var alignment = Alignment(rules, elements, index, container);
            if (alignment is null || !IsBottom(alignment) || Avoids(rules, elements, index))
            {
                continue;
            }

            return Task.FromResult(Refuse(row, elements[index], alignment));
        }

        return Task.FromResult(TemplateCheckResult.Pass());
    }

    /// <summary>
    /// Whether an element is a demonstrated grid container: a placeable display value
    /// makes it one, and a display this model cannot place may remove it, so the defect
    /// is not demonstrated.
    /// </summary>
    private static bool IsGrid(IReadOnlyList<Rule> rules, IReadOnlyList<DetailElement> elements, int index)
    {
        var display = Winner(rules, elements, index, DisplayProperties);
        return display is not null
            && PdfConstraintRules.GridContainerValues.Contains(Normalize(display.Value))
            && !HasPossible(rules, elements, index, DisplayProperties);
    }

    /// <summary>
    /// The alignment that decides an item: its own rule when it declares one that is
    /// not the initial value, else the container's. An alignment either of them may
    /// paint through a construct this model cannot place is undecided, so no
    /// demonstrated value is returned (C.1's closing sentence, D5).
    /// </summary>
    private static Rule? Alignment(
        IReadOnlyList<Rule> rules,
        IReadOnlyList<DetailElement> elements,
        int item,
        int container)
    {
        if (HasPossible(rules, elements, item, ItemAlignmentProperties))
        {
            // The item may paint an alignment the model cannot read, so its alignment
            // is undecided and the container's value demonstrates nothing.
            return null;
        }

        var own = Winner(rules, elements, item, ItemAlignmentProperties);
        if (own is not null && !Defers(own.Value))
        {
            return own;
        }

        if (HasPossible(rules, elements, container, ContainerAlignmentProperties))
        {
            // The item defers to a container alignment the model cannot read, so where
            // the item sits is undecided.
            return null;
        }

        return Winner(rules, elements, container, ContainerAlignmentProperties);
    }

    /// <summary>Whether an alignment value asks for the container's own alignment.</summary>
    private static bool Defers(string value) => Normalize(value) is "auto" or "initial" or "unset" or "revert";

    /// <summary>
    /// Whether the winning alignment puts the item at the block-axis end. The
    /// <c>place-*</c> properties carry the block component first, and a <c>safe</c> or
    /// <c>unsafe</c> prefix belongs to it.
    /// </summary>
    private static bool IsBottom(Rule alignment)
    {
        var value = Normalize(alignment.Value);
        var block = alignment.Property is "place-self" or "place-items"
            ? BlockComponent(value)
            : value;
        return PdfConstraintRules.BottomAlignmentValues.Contains(block);
    }

    private static string BlockComponent(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        return tokens[0] is "safe" or "unsafe" && tokens.Length > 1
            ? tokens[0] + " " + tokens[1]
            : tokens[0];
    }

    /// <summary>Whether a declaration forbids the break on the item, placeably or not.</summary>
    private static bool Avoids(IReadOnlyList<Rule> rules, IReadOnlyList<DetailElement> elements, int item)
    {
        if (rules.Any(rule => rule.Possible
            && PdfConstraintRules.BreakAvoidingProperties.Contains(rule.Property)
            && Applies(rule, elements, item)
            && CouldAvoid(rule.Value)))
        {
            // A break this model cannot place may keep the row from splitting, so the
            // defect is not demonstrated.
            return true;
        }

        var winner = Winner(rules, elements, item, PdfConstraintRules.BreakAvoidingProperties);
        return winner is not null && PdfConstraintRules.BreakAvoidingValues.Contains(Normalize(winner.Value));
    }

    private static bool CouldAvoid(string value) =>
        PdfConstraintRules.BreakAvoidingValues.Contains(Normalize(value))
        || HasReference(value)
        || CssWideKeyword(value) is "inherit" or "unset" or "revert-layer";

    /// <summary>The winning declaration of one of the properties, on one element.</summary>
    private static Rule? Winner(
        IReadOnlyList<Rule> rules,
        IReadOnlyList<DetailElement> elements,
        int element,
        IReadOnlySet<string> properties)
    {
        Rule? winner = null;
        foreach (var rule in rules)
        {
            if (rule.Possible || !properties.Contains(rule.Property) || !Applies(rule, elements, element))
            {
                continue;
            }

            if (winner is null || Compare(rule, winner) > 0)
            {
                winner = rule;
            }
        }

        return winner;
    }

    /// <summary>Whether a declaration the model cannot place reaches the element.</summary>
    private static bool HasPossible(
        IReadOnlyList<Rule> rules,
        IReadOnlyList<DetailElement> elements,
        int element,
        IReadOnlySet<string> properties) =>
        rules.Any(rule => rule.Possible && properties.Contains(rule.Property) && Applies(rule, elements, element));

    private static bool Applies(Rule rule, IReadOnlyList<DetailElement> elements, int element) =>
        rule.Inline ? rule.Element == element : rule.Subject.Matches(elements[element]);

    /// <summary>The declarations of the four property families the rule reads, from the three sources.</summary>
    private static List<Rule> Collect(
        TemplateCandidate candidate,
        PackageView view,
        TemplateParts? parts,
        IReadOnlyList<DetailElement> elements)
    {
        var rules = new List<Rule>();

        var styles = candidate.Manifest.Outputs?.Html?.Styles ?? [];
        for (var index = 0; index < styles.Count; index++)
        {
            var path = styles[index];
            if (!TryRead(view, path, out var text))
            {
                continue;
            }

            foreach (var declaration in StylesheetScan.Read(path, text, DeclarationSource.Linked, [index]))
            {
                Add(rules, declaration, origin: 0, element: -1);
            }
        }

        var partPaths = PartPaths(parts);
        for (var index = 0; index < partPaths.Count; index++)
        {
            var path = partPaths[index];
            if (!TryRead(view, path, out var text))
            {
                continue;
            }

            var ordinal = 0;
            foreach (var block in StylesheetScan.Blocks(text))
            {
                var padded = block.Line <= 1
                    ? block.Content
                    : new string('\n', block.Line - 1) + block.Content;
                foreach (var declaration in StylesheetScan.Read(path, padded, DeclarationSource.PartStyle, [index, ordinal]))
                {
                    Add(rules, declaration, origin: 1, element: -1);
                }

                ordinal++;
            }
        }

        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index].Style is not { } style)
            {
                continue;
            }

            foreach (var declaration in style.Declarations)
            {
                Add(rules, declaration, origin: 2, element: index);
            }
        }

        return rules;
    }

    private static void Add(List<Rule> target, StyleDeclaration declaration, int origin, int element)
    {
        if (!Properties.Contains(declaration.Property))
        {
            // A declaration of any other property is not about the layout and carries no weight.
            return;
        }

        if (origin == 2)
        {
            target.Add(new Rule(
                declaration.File,
                declaration.Line,
                declaration.Property,
                declaration.Value,
                Subject.None,
                default,
                declaration.Importance,
                origin,
                declaration.Order,
                element,
                Inline: true,
                Possible: Unreadable(declaration)));
            return;
        }

        if (declaration.Selector.Length == 0)
        {
            // A declaration no style rule carries names a resource or a page box, never an element.
            return;
        }

        if (declaration.Condition is DeclarationCondition.Screen or DeclarationCondition.OtherType)
        {
            // The document is printed in the print medium: a screen rule paints no page.
            return;
        }

        var conditioned = declaration.Condition is DeclarationCondition.Feature
            or DeclarationCondition.Supports
            or DeclarationCondition.Container
            or DeclarationCondition.Layer;
        var unreadable = Unreadable(declaration);
        foreach (var selector in StylesheetScan.Selectors(declaration.Selector))
        {
            var subject = Subject.Parse(selector);
            target.Add(new Rule(
                declaration.File,
                declaration.Line,
                declaration.Property,
                declaration.Value,
                subject,
                Specificity.Of(selector),
                declaration.Importance,
                origin,
                declaration.Order,
                Element: -1,
                Inline: false,
                Possible: conditioned || unreadable || subject.Unplaceable || subject.Dynamic));
        }
    }

    /// <summary>Whether a value is one the model cannot decide.</summary>
    private static bool Unreadable(StyleDeclaration declaration) =>
        HasReference(declaration.Value)
        || CssWideKeyword(declaration.Value) is "inherit" or "unset" or "revert-layer";

    private static bool HasReference(string value) =>
        value.Contains("var(", StringComparison.OrdinalIgnoreCase)
        || value.Contains("env(", StringComparison.OrdinalIgnoreCase);

    private static string? CssWideKeyword(string value)
    {
        var keyword = Normalize(value);
        return PdfConstraintRules.CssWideKeywords.Contains(keyword) ? keyword : null;
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool TryRead(PackageView view, string path, out string text)
    {
        text = string.Empty;
        if (!view.Exists(path))
        {
            return false;
        }

        try
        {
            text = view.ReadText(path);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The parts of the PDF body, in the order the manifest declares them.</summary>
    private static List<string> PartPaths(TemplateParts? parts)
    {
        var paths = new List<string>();
        if (parts is null)
        {
            return paths;
        }

        foreach (var path in new[]
        {
            parts.DocumentStart,
            parts.GroupStart,
            parts.Row,
            parts.GroupEnd,
            parts.DocumentEnd,
        })
        {
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>The cascade key of §6 B.1 step 4, applied to the layout declarations.</summary>
    private static int Compare(Rule first, Rule second)
    {
        var importance = first.Importance.CompareTo(second.Importance);
        if (importance != 0)
        {
            return importance;
        }

        var inline = (first.Inline ? 1 : 0).CompareTo(second.Inline ? 1 : 0);
        if (inline != 0)
        {
            return inline;
        }

        var specificity = first.Specificity.CompareTo(second.Specificity);
        if (specificity != 0)
        {
            return specificity;
        }

        var origin = first.Origin.CompareTo(second.Origin);
        return origin != 0 ? origin : Appearance(first.Order, second.Order);
    }

    private static int Appearance(IReadOnlyList<int> first, IReadOnlyList<int> second)
    {
        var shared = Math.Min(first.Count, second.Count);
        for (var index = 0; index < shared; index++)
        {
            var compared = first[index].CompareTo(second[index]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return first.Count.CompareTo(second.Count);
    }

    private static TemplateCheckResult Refuse(string part, DetailElement cell, Rule alignment) =>
        TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            TemplateRefusal.Echo(part),
            "the cell " + Describe(cell) + " may split across pages and is aligned to the bottom in the grid layout ("
                + TemplateRefusal.Echo(alignment.File)
                + " line "
                + TemplateRefusal.Number(alignment.Line)
                + ", "
                + TemplateRefusal.Echo(alignment.Property)
                + ": "
                + TemplateRefusal.Echo(alignment.Value)
                + ").",
            "Align it to the top.");

    /// <summary>The cell's identity as AC 8's message names it: its class, else its id, else its tag and position.</summary>
    private static string Describe(DetailElement cell)
    {
        if (cell.Classes.Count > 0)
        {
            return "class \"" + TemplateRefusal.Echo(cell.Classes[0]) + "\"";
        }

        if (cell.Id.Length > 0)
        {
            return "id \"" + TemplateRefusal.Echo(cell.Id) + "\"";
        }

        return "tag \"" + TemplateRefusal.Echo(cell.Tag) + "\" at position " + TemplateRefusal.Number(cell.Position);
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

    /// <summary>One declaration of the layout, with everything the cascade ranks it by.</summary>
    private sealed record Rule(
        string File,
        int Line,
        string Property,
        string Value,
        Subject Subject,
        Specificity Specificity,
        DeclarationImportance Importance,
        int Origin,
        IReadOnlyList<int> Order,
        int Element,
        bool Inline,
        bool Possible);
}
