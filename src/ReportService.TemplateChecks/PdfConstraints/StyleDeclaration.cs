namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>Where a declaration was written: a stylesheet the manifest lists, a part's own style element, or an element's attribute.</summary>
public enum DeclarationSource
{
    /// <summary>A stylesheet the manifest lists.</summary>
    Linked,

    /// <summary>A style element inside a part.</summary>
    PartStyle,

    /// <summary>A style attribute of an element.</summary>
    Inline,
}

/// <summary>Whether a declaration carries the important flag.</summary>
public enum DeclarationImportance
{
    /// <summary>No flag.</summary>
    Normal,

    /// <summary>The declaration ends in <c>!important</c>.</summary>
    Important,
}

/// <summary>The at-rule a declaration sits under, as far as a static read can tell.</summary>
public enum DeclarationCondition
{
    /// <summary>No at-rule, or one that says nothing about the medium.</summary>
    None,

    /// <summary>Printed media.</summary>
    Print,

    /// <summary>Every medium.</summary>
    All,

    /// <summary>Screen media.</summary>
    Screen,

    /// <summary>A medium that is neither print, screen nor all.</summary>
    OtherType,

    /// <summary>A media query with a feature condition.</summary>
    Feature,

    /// <summary>A supports condition.</summary>
    Supports,

    /// <summary>A container condition.</summary>
    Container,

    /// <summary>A cascade layer.</summary>
    Layer,
}

/// <summary>One style element of a part: its content and the line that content starts on.</summary>
/// <param name="Content">The text between the element's open and close tags.</param>
/// <param name="Line">The 1-based line the content starts on, inside the part.</param>
public sealed record StyleBlock(string Content, int Line);

/// <summary>
/// One <c>property: value</c> declaration of a package's own text, with where it was
/// written, the rule it belongs to and the subject that rule names, the origin it
/// sits in and its position there, its importance and the at-rule above it.
/// </summary>
/// <param name="File">The package-relative path of the text the declaration was read from.</param>
/// <param name="Line">The 1-based line of the property.</param>
/// <param name="Selector">The selector of the rule, whole; empty inside an at-rule block that is not a style rule.</param>
/// <param name="Subject">The rightmost compound of the selector, in a selector list too; empty when there is none to name.</param>
/// <param name="Source">Where the text came from.</param>
/// <param name="Order">The declaration's position in the author origin, compared left to right.</param>
/// <param name="Importance">Whether the declaration is important.</param>
/// <param name="Condition">The at-rule above the declaration.</param>
/// <param name="Property">The lower-cased property.</param>
/// <param name="Value">The value, without comments, a trailing important flag or surrounding spaces.</param>
public sealed record StyleDeclaration(
    string File,
    int Line,
    string Selector,
    string Subject,
    DeclarationSource Source,
    IReadOnlyList<int> Order,
    DeclarationImportance Importance,
    DeclarationCondition Condition,
    string Property,
    string Value);
