using System.Text;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>
/// A PDF version whose detail text names no font family is refused: the answer is the
/// cascade the engine itself applies, read off the version's own text and the elements
/// its row part emits. Every declaration of the family is collected with the source it
/// came from — a linked stylesheet, a part's own style element, an element's attribute
/// — matched against one element rather than a pool of tokens, grouped as direct,
/// ancestor, inherited or possible, and ranked by importance, inline strength, the
/// specificity triple and the order of appearance. A text-bearing element is refused
/// when its winner names no family or when nothing reaches it at all; a construct this
/// model cannot place is recorded and never refuses.
/// </summary>
public sealed class NamedFontFamilyCheck : ITemplateVersionCheck
{
    /// <summary>The rule identifier this refusal is counted under, carried to the take's log.</summary>
    public const string RuleId = TemplateRefusal.Rules.FontFamily;

    private readonly TemplateCheckSession _session;

    /// <summary>Creates the check over the take's shared session.</summary>
    /// <param name="session">The session the candidate's package is read through.</param>
    public NamedFontFamilyCheck(TemplateCheckSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <inheritdoc />
    public string Name => "NamedFontFamilyCheck";

    /// <inheritdoc />
    public Task<TemplateCheckResult> CheckAsync(TemplateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = candidate.Manifest;
        var pdf = manifest.Outputs?.Pdf;
        if (pdf is null)
        {
            // The family of the detail text is a PDF constraint: a version that prints no
            // document is not asked for one.
            return Task.FromResult(TemplateCheckResult.Pass());
        }

        var parts = pdf.Parts ?? manifest.Outputs?.Html?.Parts;
        var row = parts?.Row;
        if (string.IsNullOrEmpty(row))
        {
            // The detail text is not in a part this version declares; refusing would refuse
            // a version whose text is printed somewhere the rule cannot see.
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

        var cascade = new Cascade(elements, Collect(candidate, view, parts, elements));
        for (var index = 0; index < elements.Count; index++)
        {
            if (!elements[index].TextBearing)
            {
                continue;
            }

            var verdict = cascade.Resolve(index);
            if (verdict.State == VerdictState.Absent)
            {
                return Task.FromResult(NoRule(row));
            }

            if (verdict.State == VerdictState.Generic)
            {
                return Task.FromResult(Unnamed(row, verdict));
            }
        }

        return Task.FromResult(TemplateCheckResult.Pass());
    }

    private static TemplateCheckResult NoRule(string part) =>
        TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            TemplateRefusal.Echo(part),
            "the detail text names no font family; no rule that applies to it declares font-family.",
            "Name the family the report is measured with.");

    private static TemplateCheckResult Unnamed(string part, Verdict verdict)
    {
        var declaration = verdict.Declaration!;
        var keyword = CssWideKeyword(declaration.Value) ?? FirstGeneric(declaration.Value);
        var where = declaration.Source == DeclarationSource.Inline
            ? "the element that carries the attribute"
            : verdict.Inherited
                ? "the rule the detail text inherits from"
                : "the rule that governs its cells";
        var written = TemplateRefusal.Echo(declaration.File)
            + " line "
            + TemplateRefusal.Number(declaration.Line)
            + " sets only ";
        var how = CssWideKeyword(declaration.Value) is "initial" or "revert"
            ? "the CSS-wide keyword \"" + TemplateRefusal.Echo(keyword) + "\" on " + where + ", which asks for the default family."
            : "the generic keyword \"" + TemplateRefusal.Echo(keyword) + "\" on " + where + ".";

        return TemplateRefusal.Refuse(
            TemplateCheckCodes.TemplateInvalid,
            TemplateRefusal.Echo(part),
            "the detail text names no font family; " + written + how,
            "Name the family the report is measured with, on a rule that applies to the detail text.");
    }

    /// <summary>
    /// The declarations of the family the version writes, with the source, the subject,
    /// the specificity and the position each one carries into the cascade.
    /// </summary>
    private static List<Declaration> Collect(
        TemplateCandidate candidate,
        PackageView view,
        TemplateParts? parts,
        IReadOnlyList<DetailElement> elements)
    {
        var declarations = new List<Declaration>();

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
                Add(declarations, declaration, origin: 0, element: -1, elements);
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
                    Add(declarations, declaration, origin: 1, element: -1, elements);
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
                Add(declarations, declaration, origin: 2, element: index, elements);
            }
        }

        return declarations;
    }

    private static void Add(
        List<Declaration> target,
        StyleDeclaration declaration,
        int origin,
        int element,
        IReadOnlyList<DetailElement> elements)
    {
        if (declaration.Property != PdfConstraintRules.FontFamilyProperty
            && declaration.Property != PdfConstraintRules.FontShorthandProperty)
        {
            // A declaration of any other property is not about the family and carries no weight.
            return;
        }

        if (origin == 2)
        {
            target.Add(new Declaration(
                declaration.File,
                declaration.Line,
                DeclarationSource.Inline,
                default,
                declaration.Importance,
                declaration.Condition,
                declaration.Property,
                declaration.Value,
                origin,
                declaration.Order,
                Subject.None,
                element,
                Placeable(declaration),
                Inherited: false));
            return;
        }

        if (declaration.Selector.Length == 0)
        {
            // A declaration no style rule carries — inside @font-face, @page, @counter-style
            // or a keyframes frame — names a resource or a page box, never an element.
            return;
        }

        if (declaration.Condition is DeclarationCondition.Screen or DeclarationCondition.OtherType)
        {
            // The document is printed in the print medium: a screen rule paints no page.
            return;
        }

        foreach (var selector in StylesheetScan.Selectors(declaration.Selector))
        {
            var subject = Subject.Parse(selector);
            target.Add(new Declaration(
                declaration.File,
                declaration.Line,
                declaration.Source,
                Specificity.Of(selector),
                declaration.Importance,
                declaration.Condition,
                declaration.Property,
                declaration.Value,
                origin,
                declaration.Order,
                subject,
                Element: -1,
                Placeable(declaration),
                Inherited: !subject.Dynamic
                    && !elements.Any(candidate => subject.Matches(candidate))));
        }
    }

    /// <summary>Whether the declaration's construct and value are ones this model can place.</summary>
    private static bool Placeable(StyleDeclaration declaration) =>
        declaration.Property == PdfConstraintRules.FontFamilyProperty
        && declaration.Condition is DeclarationCondition.None or DeclarationCondition.Print or DeclarationCondition.All
        && !HasReference(declaration.Value)
        && CssWideKeyword(declaration.Value) != "revert-layer";

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
            // A text the walk cannot read is recorded as far as it goes and never refused.
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

    private static bool HasReference(string value) =>
        value.Contains("var(", StringComparison.OrdinalIgnoreCase)
        || value.Contains("env(", StringComparison.OrdinalIgnoreCase);

    /// <summary>The CSS-wide keyword a value holds, or null when it holds none.</summary>
    private static string? CssWideKeyword(string value)
    {
        foreach (var part in Names(value))
        {
            if (PdfConstraintRules.CssWideKeywords.Contains(part))
            {
                return part;
            }
        }

        return null;
    }

    /// <summary>The first generic keyword a value holds.</summary>
    private static string FirstGeneric(string value)
    {
        foreach (var part in Names(value))
        {
            if (PdfConstraintRules.GenericFontFamilies.Contains(part))
            {
                return part;
            }
        }

        return string.Empty;
    }

    /// <summary>Whether a value holds at least one name that is neither a generic nor a CSS-wide keyword.</summary>
    private static bool NamesFamily(string value)
    {
        foreach (var part in Names(value))
        {
            if (part.Length > 0
                && !PdfConstraintRules.GenericFontFamilies.Contains(part)
                && !PdfConstraintRules.CssWideKeywords.Contains(part))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The family names a value lists, one per comma, with quotes and spaces removed.</summary>
    private static IEnumerable<string> Names(string value)
    {
        foreach (var raw in value.Split(','))
        {
            var part = raw.Trim();
            if (part.Length >= 2
                && ((part[0] == '"' && part[^1] == '"') || (part[0] == '\'' && part[^1] == '\'')))
            {
                part = part[1..^1].Trim();
            }

            yield return part;
        }
    }

    /// <summary>One declaration of the family, with everything the cascade ranks it by.</summary>
    private sealed record Declaration(
        string File,
        int Line,
        DeclarationSource Source,
        Specificity Specificity,
        DeclarationImportance Importance,
        DeclarationCondition Condition,
        string Property,
        string Value,
        int Origin,
        IReadOnlyList<int> Order,
        Subject Subject,
        int Element,
        bool PlaceableValue,
        bool Inherited);

    /// <summary>What one text-bearing element's winning family is.</summary>
    private enum VerdictState
    {
        /// <summary>A name that is neither generic nor a keyword.</summary>
        Named,

        /// <summary>Only generic keywords, or the engine's own default family.</summary>
        Generic,

        /// <summary>A construct or value this model cannot read.</summary>
        Undecided,

        /// <summary>Nothing applies, nothing inherits, and no possible declaration is compatible.</summary>
        Absent,
    }

    /// <summary>The element's winner, and the declaration that decided it when one did.</summary>
    private sealed record Verdict(
        VerdictState State,
        Declaration? Declaration,
        bool Inherited = false,
        bool FromInherited = false);

    /// <summary>The cascade of one row part, resolved per text-bearing element.</summary>
    private sealed class Cascade
    {
        private static readonly Comparer<Declaration> Key = Comparer<Declaration>.Create(Compare);

        private readonly IReadOnlyList<DetailElement> _elements;
        private readonly IReadOnlyList<Declaration> _declarations;
        private readonly IReadOnlyList<Declaration> _inherited;
        private readonly Verdict?[] _memo;

        public Cascade(IReadOnlyList<DetailElement> elements, IReadOnlyList<Declaration> declarations)
        {
            _elements = elements;
            _declarations = declarations;
            _inherited = [.. declarations.Where(declaration => declaration.Inherited).Order(Key)];
            _memo = new Verdict?[elements.Count];
        }

        public Verdict Resolve(int index)
        {
            if (_memo[index] is { } resolved)
            {
                return resolved;
            }

            var verdict = Compute(index);
            _memo[index] = verdict;
            return verdict;
        }

        private Verdict Compute(int index)
        {
            var element = _elements[index];
            var directs = _declarations.Where(declaration => IsDirect(declaration, index)).ToArray();
            if (directs.Length > 0)
            {
                var winner = directs.Max(Key)!;
                return IsInheritForm(winner.Value)
                    ? InheritFrom(index)
                    : new Verdict(Judge(winner), winner);
            }

            if (element.Parent is { } parent)
            {
                var resolved = Resolve(parent);

                // An ancestor's own declaration is the value this element would inherit; an
                // ancestor that merely fell back to the inherited group is that group's value
                // again, and a declaration compatible with this element that the model cannot
                // place must be weighed before it is accepted here.
                if (resolved.State != VerdictState.Absent && !resolved.FromInherited)
                {
                    return resolved;
                }
            }

            if (HasPossible(index))
            {
                return new Verdict(VerdictState.Undecided, Declaration: null);
            }

            if (_inherited.Count > 0)
            {
                var winner = _inherited[^1];
                return IsInheritForm(winner.Value)
                    ? new Verdict(VerdictState.Undecided, Declaration: null, FromInherited: true)
                    : new Verdict(Judge(winner), winner, Inherited: true, FromInherited: true);
            }

            return new Verdict(VerdictState.Absent, Declaration: null);
        }

        private Verdict InheritFrom(int index)
        {
            for (var parent = _elements[index].Parent; parent is { } ancestor; parent = _elements[ancestor].Parent)
            {
                if (_declarations.Any(declaration => IsDirect(declaration, ancestor)))
                {
                    return Resolve(ancestor);
                }
            }

            if (_inherited.Count > 0)
            {
                var winner = _inherited[^1];
                return IsInheritForm(winner.Value)
                    ? new Verdict(VerdictState.Undecided, Declaration: null, FromInherited: true)
                    : new Verdict(Judge(winner), winner, Inherited: true, FromInherited: true);
            }

            return new Verdict(VerdictState.Undecided, Declaration: null);
        }

        private bool HasPossible(int index)
        {
            for (var current = (int?)index; current is { } position; current = _elements[position].Parent)
            {
                if (_declarations.Any(declaration => IsPossible(declaration, position)))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsDirect(Declaration declaration, int index) =>
            declaration.Source == DeclarationSource.Inline
                ? declaration.Element == index
                : declaration.PlaceableValue
                    && !declaration.Subject.Unplaceable
                    && !declaration.Subject.Dynamic
                    && declaration.Subject.Matches(_elements[index]);

        private bool IsPossible(Declaration declaration, int index)
        {
            if (declaration.Source == DeclarationSource.Inline)
            {
                return false;
            }

            if (declaration.Subject.Dynamic)
            {
                return true;
            }

            return declaration.Subject.Unplaceable
                ? declaration.Subject.Matches(_elements[index])
                : declaration.Subject.Matches(_elements[index]) && !declaration.PlaceableValue;
        }

        private static bool IsInheritForm(string value) =>
            CssWideKeyword(value) is "inherit" or "unset";

        private static VerdictState Judge(Declaration declaration)
        {
            if (declaration.Subject.Unplaceable)
            {
                // The group is the subject's and the value is the construct's: an
                // unplaceable construct on a subject that matches no emitted element
                // leaves the value undecided and never refuses.
                return VerdictState.Undecided;
            }

            if (declaration.Property == PdfConstraintRules.FontShorthandProperty)
            {
                // The shorthand may carry the family beside a size and a weight this model
                // does not read: refusing it would refuse a version that names one.
                return VerdictState.Undecided;
            }

            if (HasReference(declaration.Value))
            {
                return VerdictState.Undecided;
            }

            var keyword = CssWideKeyword(declaration.Value);
            if (keyword is "initial" or "revert")
            {
                return VerdictState.Generic;
            }

            if (keyword is "revert-layer" or "inherit" or "unset")
            {
                return VerdictState.Undecided;
            }

            if (declaration.Condition is DeclarationCondition.Feature
                or DeclarationCondition.Supports
                or DeclarationCondition.Container
                or DeclarationCondition.Layer)
            {
                return VerdictState.Undecided;
            }

            return NamesFamily(declaration.Value) ? VerdictState.Named : VerdictState.Generic;
        }

        private static int Compare(Declaration? first, Declaration? second)
        {
            if (ReferenceEquals(first, second))
            {
                return 0;
            }

            if (first is null)
            {
                return -1;
            }

            if (second is null)
            {
                return 1;
            }

            var importance = first.Importance.CompareTo(second.Importance);
            if (importance != 0)
            {
                return importance;
            }

            var inline = Inline(first).CompareTo(Inline(second));
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

        private static int Inline(Declaration declaration) =>
            declaration.Source == DeclarationSource.Inline ? 1 : 0;

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
    }
}

/// <summary>CSS's specificity triple: ids, then classes and attributes and pseudo-classes, then types and pseudo-elements.</summary>
internal readonly record struct Specificity(int Ids, int Classes, int Types) : IComparable<Specificity>
{
    /// <inheritdoc />
    public int CompareTo(Specificity other)
    {
        var ids = Ids.CompareTo(other.Ids);
        if (ids != 0)
        {
            return ids;
        }

        var classes = Classes.CompareTo(other.Classes);
        return classes != 0 ? classes : Types.CompareTo(other.Types);
    }

    /// <summary>The triple of one selector, counted over the selector as written.</summary>
    public static Specificity Of(string selector)
    {
        var ids = 0;
        var classes = 0;
        var types = 0;
        var start = true;
        for (var index = 0; index < selector.Length; index++)
        {
            var character = selector[index];
            if (character is ' ' or '\t' or '\n' or '\r' or '>' or '+' or '~')
            {
                start = true;
                continue;
            }

            if (character == '*')
            {
                start = false;
                continue;
            }

            if (character == '#')
            {
                ids++;
                index = Name(selector, index + 1) - 1;
                start = false;
                continue;
            }

            if (character == '.')
            {
                classes++;
                index = Name(selector, index + 1) - 1;
                start = false;
                continue;
            }

            if (character == '[')
            {
                classes++;
                index = Close(selector, index + 1, ']') - 1;
                start = false;
                continue;
            }

            if (character == ':')
            {
                if (index + 1 < selector.Length && selector[index + 1] == ':')
                {
                    types++;
                    index = Name(selector, index + 2) - 1;
                }
                else
                {
                    classes++;
                    index = Name(selector, index + 1) - 1;
                }

                start = false;
                continue;
            }

            if (char.IsLetter(character) && start)
            {
                types++;
                index = Name(selector, index) - 1;
            }

            start = false;
        }

        return new Specificity(ids, classes, types);
    }

    private static int Name(string text, int index)
    {
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '-' or '_'))
        {
            index++;
        }

        return index;
    }

    private static int Close(string text, int index, char terminator)
    {
        while (index < text.Length && text[index] != terminator)
        {
            index++;
        }

        return index;
    }
}

/// <summary>The subject compound of a selector, parsed into what matches an emitted element.</summary>
internal sealed record Subject(
    string Tag,
    IReadOnlyList<string> Classes,
    string Id,
    bool Dynamic,
    bool Unplaceable,
    bool MatchesNothing)
{
    /// <summary>The subject of an inline declaration: its element is the subject.</summary>
    public static Subject None { get; } = new(string.Empty, [], string.Empty, Dynamic: false, Unplaceable: false, MatchesNothing: false);

    /// <summary>The subject of one selector.</summary>
    public static Subject Parse(string selector)
    {
        if (HasExpression(selector))
        {
            return new Subject(string.Empty, [], string.Empty, Dynamic: true, Unplaceable: false, MatchesNothing: false);
        }

        var compound = Rightmost(selector);
        var tag = string.Empty;
        var classes = new List<string>();
        var id = string.Empty;
        var unplaceable = false;
        var matchesNothing = false;
        var index = 0;

        if (index < compound.Length && (char.IsLetter(compound[index]) || compound[index] == '*'))
        {
            if (compound[index] == '*')
            {
                tag = "*";
                index++;
            }
            else
            {
                var start = index;
                while (index < compound.Length && (char.IsLetterOrDigit(compound[index]) || compound[index] is '-' or '_'))
                {
                    index++;
                }

                tag = compound[start..index].ToLowerInvariant();
            }
        }

        while (index < compound.Length)
        {
            var character = compound[index];
            if (character == '.')
            {
                var start = ++index;
                while (index < compound.Length && (char.IsLetterOrDigit(compound[index]) || compound[index] is '-' or '_'))
                {
                    index++;
                }

                if (index > start)
                {
                    classes.Add(compound[start..index]);
                }

                continue;
            }

            if (character == '#')
            {
                var start = ++index;
                while (index < compound.Length && (char.IsLetterOrDigit(compound[index]) || compound[index] is '-' or '_'))
                {
                    index++;
                }

                id = compound[start..index];
                continue;
            }

            if (character == '[')
            {
                unplaceable = true;
                index = Close(compound, index);
                continue;
            }

            if (character == ':')
            {
                if (index + 1 < compound.Length && compound[index + 1] == ':')
                {
                    unplaceable = true;
                    index += 2;
                    while (index < compound.Length && (char.IsLetterOrDigit(compound[index]) || compound[index] is '-' or '_'))
                    {
                        index++;
                    }

                    continue;
                }

                var start = ++index;
                while (index < compound.Length && (char.IsLetterOrDigit(compound[index]) || compound[index] is '-' or '_'))
                {
                    index++;
                }

                if (compound[start..index].Equals("root", StringComparison.OrdinalIgnoreCase))
                {
                    matchesNothing = true;
                }
                else
                {
                    unplaceable = true;
                }

                continue;
            }

            index++;
        }

        return new Subject(tag, classes, id, Dynamic: false, Unplaceable: unplaceable, MatchesNothing: matchesNothing);
    }

    /// <summary>Whether the subject's literal parts are all carried by one emitted element.</summary>
    public bool Matches(DetailElement element)
    {
        if (MatchesNothing)
        {
            return false;
        }

        if (Tag.Length > 0 && !Tag.Equals("*", StringComparison.Ordinal)
            && !Tag.Equals(element.Tag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var token in Classes)
        {
            if (!element.Classes.Contains(token, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return Id.Length == 0 || Id.Equals(element.Id, StringComparison.Ordinal);
    }

    private static string Rightmost(string selector)
    {
        string? rightmost = null;
        foreach (var compound in selector.Split([' ', '\t', '\n', '\r', '>', '+', '~', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            rightmost = compound;
        }

        return rightmost ?? string.Empty;
    }

    private static int Close(string text, int index)
    {
        while (index < text.Length && text[index] != ']')
        {
            index++;
        }

        return index + 1;
    }

    private static bool HasExpression(string text) =>
        text.Contains("{{", StringComparison.Ordinal)
        || text.Contains("{%", StringComparison.Ordinal)
        || text.Contains("{#", StringComparison.Ordinal);
}

