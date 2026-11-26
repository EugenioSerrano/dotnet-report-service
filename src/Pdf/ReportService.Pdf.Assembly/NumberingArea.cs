namespace ReportService.Pdf.Assembly;

/// <summary>
/// The rectangle the template reserves for the global page number, declared in
/// millimetres in the page's displayed frame: its origin is the page's top-left
/// corner and its axes are the ones a reader sees. The assembly converts the
/// rectangle to the points it draws in; the manifest keeps the millimetres the
/// template author wrote.
/// </summary>
/// <param name="LeftMm">The distance from the page's left edge to the area.</param>
/// <param name="TopMm">The distance from the page's top edge to the area.</param>
/// <param name="WidthMm">The area's width.</param>
/// <param name="HeightMm">The area's height.</param>
public sealed record NumberingArea(double LeftMm, double TopMm, double WidthMm, double HeightMm)
{
    /// <summary>How many points one millimetre is.</summary>
    public const double PointsPerMillimetre = 72d / 25.4d;

    /// <summary>The area's left edge, in points.</summary>
    public double LeftPt => LeftMm * PointsPerMillimetre;

    /// <summary>The area's top edge, in points.</summary>
    public double TopPt => TopMm * PointsPerMillimetre;

    /// <summary>The area's width, in points.</summary>
    public double WidthPt => WidthMm * PointsPerMillimetre;

    /// <summary>The area's height, in points.</summary>
    public double HeightPt => HeightMm * PointsPerMillimetre;

    /// <summary>Whether both dimensions are positive.</summary>
    public bool IsPositive => WidthMm > 0d && HeightMm > 0d;
}
