using System.Text;
using ReportService.Pdf.Assembly;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The fit measurement: the stamp at the document ceiling's page count must fit the
/// reserved area at the declared font and size, and a wording that would overflow
/// it is refused before anything is printed.
/// </summary>
public sealed class NumberingFitTests
{
    [Fact]
    public void A_wording_that_fits_is_measured_inside_its_area()
    {
        var numbering = AssemblyFixture.Contract();

        var fit = NumberingFit.Measure(numbering);

        Assert.True(fit.Fits);
        Assert.True(fit.WidthPt <= fit.AreaWidthPt);
        Assert.True(fit.HeightPt <= fit.AreaHeightPt);
        Assert.Equal("Page 10000 of 10000", numbering.TextFor(10_000, 10_000));
    }

    [Fact]
    public void A_narrow_area_refuses_the_widest_stamp()
    {
        var numbering = AssemblyFixture.Contract(new NumberingArea(120, 268, 12, 8));

        var fit = NumberingFit.Measure(numbering);

        Assert.False(fit.Fits);
        Assert.Equal("stamp-overflows-area", fit.Reason);
    }

    [Fact]
    public void A_capacity_of_one_hundred_thousand_pages_refuses_an_area_that_fits_only_ten_thousand()
    {
        var probe = AssemblyFixture.Contract();
        var tenThousand = NumberingFit.Measure(probe, probe.TextFor(10_000, 10_000)).WidthPt;
        var hundredThousand = NumberingFit.Measure(probe, probe.TextFor(100_000, 100_000)).WidthPt;
        Assert.True(tenThousand < hundredThousand);
        var widthMm = (tenThousand + hundredThousand) / 2 / NumberingArea.PointsPerMillimetre;
        var numbering = AssemblyFixture.Contract(new NumberingArea(120, 268, widthMm, 8));

        var fit = NumberingFit.Measure(numbering);

        TestContext.Current.TestOutputHelper?.WriteLine(
            "reason=" + (fit.Reason ?? "null")
            + " widthPt=" + NumberingFit.Number(fit.WidthPt)
            + " areaWidthPt=" + NumberingFit.Number(fit.AreaWidthPt)
            + " width10000Pt=" + NumberingFit.Number(tenThousand)
            + " width100000Pt=" + NumberingFit.Number(hundredThousand));
        Assert.False(fit.Fits);
        Assert.Equal("stamp-overflows-area", fit.Reason);
    }

    [Fact]
    public void A_low_area_refuses_the_line_height()
    {
        var numbering = AssemblyFixture.Contract(new NumberingArea(120, 268, 80, 1));

        var fit = NumberingFit.Measure(numbering);

        Assert.False(fit.Fits);
    }

    [Fact]
    public void An_unreadable_font_is_refused()
    {
        var font = new NumberingFont("broken", "fonts/broken.ttf", 9, Encoding.ASCII.GetBytes("not a font"));
        var numbering = new PageNumbering(AssemblyFixture.Area, font, "Page {X} of {Y}", "en-US");

        var fit = NumberingFit.Measure(numbering);

        Assert.False(fit.Fits);
        Assert.Equal("font-unreadable", fit.Reason);
    }

    [Theory]
    [InlineData("Page {X} of {Y}", true)]
    [InlineData("Hoja {X} de {Y}", true)]
    [InlineData("Page {X} of {X}", false)]
    [InlineData("Page {X}", false)]
    [InlineData("Page {Y} of {X} of {X}", false)]
    [InlineData("Page {X} of {Y} {Z}", false)]
    [InlineData("Page X of Y", false)]
    [InlineData("", false)]
    public void The_wording_carries_exactly_one_placeholder_of_each(string format, bool wellFormed) =>
        Assert.Equal(wellFormed, PageNumbering.IsWellFormedFormat(format));
}
