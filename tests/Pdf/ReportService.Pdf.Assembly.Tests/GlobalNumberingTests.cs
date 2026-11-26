using ReportService.Pdf.Assembly;
using ReportService.Pdf.Verification;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The global page number: one continuous series across the whole assembled
/// document, never restarted at a part boundary, with the total of the finished
/// report, in the declared wording and culture, and only inside the reserved area.
/// </summary>
public sealed class GlobalNumberingTests
{
    [Fact]
    public async Task The_series_runs_from_one_to_the_total_across_parts()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 2),
                AssemblyFixture.CreatePart(directory, 1, 3),
                AssemblyFixture.CreatePart(directory, 2, 2),
            };
            var output = Path.Combine(directory, "assembled.pdf");
            await AssemblyFixture.AssembleAsync(parts, directory, output);

            var reading = PdfWordReader.Read(output, AssemblyFixture.Frame.Margins);
            for (var index = 0; index < reading.Pages.Count; index++)
            {
                var stamp = StampOf(reading.Pages[index], AssemblyFixture.Area);
                Assert.Equal($"Page {index + 1} of 7", stamp);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_declared_format_and_culture_are_applied_exactly()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 2) };
            var output = Path.Combine(directory, "assembled.pdf");
            await AssemblyFixture.AssembleAsync(
                parts,
                directory,
                output,
                AssemblyFixture.Contract(format: "Hoja {X} de {Y}", culture: "es-AR"));

            var reading = PdfWordReader.Read(output, AssemblyFixture.Frame.Margins);
            Assert.Equal("Hoja 1 de 2", StampOf(reading.Pages[0], AssemblyFixture.Area));
            Assert.Equal("Hoja 2 de 2", StampOf(reading.Pages[1], AssemblyFixture.Area));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Nothing_but_the_stamp_is_written_inside_the_area()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 1) };
            var output = Path.Combine(directory, "assembled.pdf");
            await AssemblyFixture.AssembleAsync(parts, directory, output);

            var reading = PdfWordReader.Read(output, AssemblyFixture.Frame.Margins);
            var inside = reading.Pages[0].Words.Where(word => Inside(word, AssemblyFixture.Area)).Select(word => word.Text).ToList();
            Assert.Equal(["Page", "1", "of", "1"], inside);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static string StampOf(PrintedPage page, NumberingArea area) =>
        string.Join(' ', page.Words.Where(word => Inside(word, area)).Select(word => word.Text));

    internal static bool Inside(PrintedWord word, NumberingArea area)
    {
        var x = (word.Left + word.Right) / 2d;
        var y = (word.Top + word.Bottom) / 2d;
        return x >= area.LeftPt && x <= area.LeftPt + area.WidthPt
            && y >= area.TopPt && y <= area.TopPt + area.HeightPt;
    }
}
