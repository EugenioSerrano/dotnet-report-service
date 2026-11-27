namespace ReportService.Pdf.Verification.PageCodes;

/// <summary>The size of the area a per-page code is drawn in, in points.</summary>
/// <param name="WidthPt">The area's width.</param>
/// <param name="HeightPt">The area's height.</param>
public readonly record struct PageCodeArea(double WidthPt, double HeightPt);

/// <summary>
/// A generator's answer at take time: the declaration's options and area are
/// accepted, with the largest content the generator can produce for them, or
/// refused with a problem and its fix.
/// </summary>
public sealed record PageCodeCheck
{
    private PageCodeCheck(byte[]? worstCase, string? problem, string? fix)
    {
        WorstCaseContent = worstCase;
        Problem = problem;
        Fix = fix;
    }

    /// <summary>Whether the generator accepts the options and the area.</summary>
    public bool Accepted => WorstCaseContent is not null;

    /// <summary>
    /// The content whose size is the largest the generator can produce for any value
    /// up to the bound it was given, with these options and this area.
    /// </summary>
    public byte[]? WorstCaseContent { get; }

    /// <summary>What the generator refuses, when it refuses.</summary>
    public string? Problem { get; }

    /// <summary>How the author fixes it, when the generator refuses.</summary>
    public string? Fix { get; }

    /// <summary>Accepts, with the worst-case content.</summary>
    /// <param name="worstCaseContent">The largest content the generator can produce.</param>
    /// <returns>The acceptance.</returns>
    public static PageCodeCheck Accept(byte[] worstCaseContent)
    {
        ArgumentNullException.ThrowIfNull(worstCaseContent);
        return new PageCodeCheck(worstCaseContent, null, null);
    }

    /// <summary>Refuses, naming the problem and the fix.</summary>
    /// <param name="problem">What is wrong.</param>
    /// <param name="fix">How to correct it.</param>
    /// <returns>The refusal.</returns>
    public static PageCodeCheck Refuse(string problem, string fix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        ArgumentException.ThrowIfNullOrWhiteSpace(fix);
        return new PageCodeCheck(null, problem, fix);
    }
}

/// <summary>A generator's content for one value, or its refusal of that value.</summary>
public sealed record PageCodeContent
{
    private PageCodeContent(byte[]? content, string? refusal)
    {
        Content = content;
        Refusal = refusal;
    }

    /// <summary>The content stream bytes, in the form's own coordinate space.</summary>
    public byte[]? Content { get; }

    /// <summary>Why the value cannot be drawn, when it cannot.</summary>
    public string? Refusal { get; }

    /// <summary>The content for the value.</summary>
    /// <param name="content">The content stream bytes.</param>
    /// <returns>The content.</returns>
    public static PageCodeContent Of(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new PageCodeContent(content, null);
    }

    /// <summary>The value cannot be drawn.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The refusal.</returns>
    public static PageCodeContent Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PageCodeContent(null, reason);
    }
}

/// <summary>
/// Draws a per-page code. A template names a generator; the deployment registers
/// it. The generator turns the value the product evaluated on a page into the
/// vector content of one form, in the form's own coordinate space (origin at the
/// lower-left corner, the area's width and height in points). The product
/// validates that content, draws it inside the template's area and, to verify the
/// page, asks the same generator for the same value again and compares the bytes:
/// a generator must therefore be deterministic and free of side effects.
/// </summary>
public interface IPageCodeGenerator
{
    /// <summary>The name a template declares to use this generator.</summary>
    string Name { get; }

    /// <summary>
    /// Accepts or refuses the declaration's options and area at take time. On
    /// acceptance it returns the largest content it can produce for any value of up
    /// to <paramref name="maxValueBytes"/> UTF-8 bytes; a generator that cannot bound
    /// its output must refuse.
    /// </summary>
    /// <param name="options">The declaration's options, unchanged.</param>
    /// <param name="area">The area's size.</param>
    /// <param name="maxValueBytes">The largest value, in UTF-8 bytes.</param>
    /// <returns>The acceptance with the worst-case content, or the refusal.</returns>
    PageCodeCheck Validate(IReadOnlyDictionary<string, string> options, PageCodeArea area, int maxValueBytes);

    /// <summary>Draws the code of one value.</summary>
    /// <param name="value">The value evaluated on the page.</param>
    /// <param name="options">The declaration's options, unchanged.</param>
    /// <param name="area">The area's size.</param>
    /// <returns>The content, or the refusal of the value.</returns>
    PageCodeContent Generate(string value, IReadOnlyDictionary<string, string> options, PageCodeArea area);
}
