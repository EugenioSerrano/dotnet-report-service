namespace ReportService.TemplatePackages.Manifest;

/// <summary>
/// The paper sizes a PDF template may declare: A4 and Letter, with the portrait
/// millimetres of each. The names are compared without ASCII case, and this type is
/// the only place in the product where a paper's size is written.
/// </summary>
public static class PaperSizes
{
    /// <summary>The ISO A4 paper, 210 by 297 millimetres in portrait.</summary>
    public const string A4 = "A4";

    /// <summary>The North American Letter paper, 215.9 by 279.4 millimetres in portrait.</summary>
    public const string Letter = "Letter";

    /// <summary>Every paper the service offers, in the order it names them.</summary>
    public static IReadOnlyList<string> Names { get; } = [A4, Letter];

    /// <summary>The portrait size of <paramref name="paper"/>, or <see langword="false"/> when it is not offered.</summary>
    /// <param name="paper">The paper as the manifest names it.</param>
    /// <param name="widthMm">The paper's width in portrait, in millimetres.</param>
    /// <param name="heightMm">The paper's height in portrait, in millimetres.</param>
    public static bool TryMillimetres(string? paper, out decimal widthMm, out decimal heightMm)
    {
        if (AsciiEquals(paper, A4))
        {
            widthMm = 210m;
            heightMm = 297m;
            return true;
        }

        if (AsciiEquals(paper, Letter))
        {
            widthMm = 215.9m;
            heightMm = 279.4m;
            return true;
        }

        widthMm = 0m;
        heightMm = 0m;
        return false;
    }

    /// <summary>Whether two names are equal without case, for the ASCII names a manifest may write.</summary>
    /// <param name="left">One name.</param>
    /// <param name="right">The other name.</param>
    internal static bool AsciiEquals(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (LowerAscii(left[index]) != LowerAscii(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static char LowerAscii(char character) =>
        character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;
}
