namespace ReportService.Templates;

/// <summary>
/// The named fragments a template package declares for one output. A block is
/// composed by invoking them in this order: the document start, then per record
/// the group start, the row and the group end, then the document end.
/// </summary>
public enum TemplatePart
{
    /// <summary>The beginning of the document, invoked once per block.</summary>
    DocumentStart,

    /// <summary>The beginning of a group, invoked before the first record of each group.</summary>
    GroupStart,

    /// <summary>One record.</summary>
    Row,

    /// <summary>The end of a group, invoked after the last record of each group.</summary>
    GroupEnd,

    /// <summary>The end of the document, invoked once per block.</summary>
    DocumentEnd,
}

/// <summary>The names the parts carry in a package manifest and in the diagnostics.</summary>
public static class TemplatePartNames
{
    /// <summary>The manifest name of the document start.</summary>
    public const string DocumentStart = "documentStart";

    /// <summary>The manifest name of a group start.</summary>
    public const string GroupStart = "groupStart";

    /// <summary>The manifest name of a row.</summary>
    public const string Row = "row";

    /// <summary>The manifest name of a group end.</summary>
    public const string GroupEnd = "groupEnd";

    /// <summary>The manifest name of the document end.</summary>
    public const string DocumentEnd = "documentEnd";

    /// <summary>The manifest name of <paramref name="part"/>.</summary>
    public static string Of(TemplatePart part) => part switch
    {
        TemplatePart.DocumentStart => DocumentStart,
        TemplatePart.GroupStart => GroupStart,
        TemplatePart.Row => Row,
        TemplatePart.GroupEnd => GroupEnd,
        _ => DocumentEnd,
    };
}
