using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using ReportService.Pdf.Assembly;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The structural verification and its known-bad controls: every failure the
/// architecture names — a missing page, a swapped pair, a lost word, a stamp
/// outside the area, a stamp wrong for its page, a changed page box, an added
/// page-dictionary key, a part whose bytes no longer match its commit — fails with
/// the verifier's reason, and a good document passes.
/// </summary>
public sealed class AssemblyVerificationTests
{
    [Fact]
    public async Task A_good_document_passes()
    {
        var setup = await SetupAsync(2, 2);
        try
        {
            var verdict = Verify(setup.Output, setup.Parts);

            Assert.True(verdict.Passed, verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    [Fact]
    public async Task A_missing_page_fails_the_page_count()
    {
        var setup = await SetupAsync(2, 2);
        try
        {
            var doctored = AssemblyFixture.Rebuild(setup.Output, Path.Combine(setup.Directory, "missing.pdf"), number => number != 3);

            var verdict = Verify(doctored, setup.Parts);

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.PageCount, verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    [Fact]
    public async Task A_swapped_pair_fails_the_order()
    {
        var setup = await SetupAsync(2, 2);
        try
        {
            var doctored = Swap(setup.Output, Path.Combine(setup.Directory, "swapped.pdf"));

            var verdict = Verify(doctored, setup.Parts);

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.Order, verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    [Fact]
    public async Task A_lost_word_fails_the_order()
    {
        var setup = await SetupAsync(2, 2);
        try
        {
            var doctored = BlankFirstPage(setup.Output, Path.Combine(setup.Directory, "lost.pdf"));

            var verdict = Verify(doctored, setup.Parts);

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.Order, verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    [Fact]
    public async Task A_stamp_outside_the_area_fails_the_stamp()
    {
        var setup = await SetupAsync(1, 2);
        try
        {
            var elsewhere = AssemblyFixture.Contract(new NumberingArea(20, 100, 80, 8));

            var verdict = AssemblyVerifier.Verify(new AssemblyVerificationRequest(
                setup.Output,
                AssemblyFixture.Expectations(setup.Parts),
                AssemblyFixture.Frame,
                elsewhere));

            Assert.False(verdict.Passed);
            Assert.True(
                verdict.Reason is AssemblyFailureReasons.Stamp or AssemblyFailureReasons.Order,
                verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    [Fact]
    public async Task A_stamp_wrong_for_its_page_fails()
    {
        var setup = await SetupAsync(1, 2);
        try
        {
            var wrong = AssemblyFixture.Contract(format: "Page {X} of 99");

            var verdict = AssemblyVerifier.Verify(new AssemblyVerificationRequest(
                setup.Output,
                AssemblyFixture.Expectations(setup.Parts),
                AssemblyFixture.Frame,
                wrong));

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.Stamp, verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    [Fact]
    public async Task A_changed_page_box_fails()
    {
        var setup = await SetupAsync(1, 2);
        try
        {
            var doctored = AssemblyFixture.Rebuild(
                setup.Output,
                Path.Combine(setup.Directory, "box.pdf"),
                _ => true,
                document => document.Pages[0].Width = XUnit.FromPoint(AssemblyFixture.Frame.WidthPt - 40));

            var verdict = Verify(doctored, setup.Parts);

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.PartPageBox, verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    [Fact]
    public async Task An_added_page_dictionary_key_fails()
    {
        var setup = await SetupAsync(1, 2);
        try
        {
            var doctored = AssemblyFixture.Rebuild(
                setup.Output,
                Path.Combine(setup.Directory, "dictionary.pdf"),
                _ => true,
                document => document.Pages[0].Elements["/CustomCheck"] = new PdfString("x"));

            var verdict = Verify(doctored, setup.Parts);

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.PageDictionary, verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    [Fact]
    public async Task A_part_whose_bytes_changed_fails()
    {
        var setup = await SetupAsync(1, 2);
        try
        {
            var expectations = AssemblyFixture.Expectations(setup.Parts)
                .Select(expectation => expectation with { Sha256 = new string('0', 64) })
                .ToList();

            var verdict = AssemblyVerifier.Verify(new AssemblyVerificationRequest(
                setup.Output,
                expectations,
                AssemblyFixture.Frame,
                AssemblyFixture.Contract()));

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.PartBytes, verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    [Fact]
    public async Task A_part_page_count_that_does_not_match_its_commit_fails()
    {
        var setup = await SetupAsync(1, 2);
        try
        {
            var expectations = AssemblyFixture.Expectations(setup.Parts)
                .Select(expectation => expectation with { PageCount = expectation.PageCount + 1 })
                .ToList();

            var verdict = AssemblyVerifier.Verify(new AssemblyVerificationRequest(
                setup.Output,
                expectations,
                AssemblyFixture.Frame,
                AssemblyFixture.Contract()));

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.PartPages, verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    [Fact]
    public async Task An_unreadable_document_fails()
    {
        var setup = await SetupAsync(1, 1);
        try
        {
            var broken = Path.Combine(setup.Directory, "broken.pdf");
            await File.WriteAllTextAsync(broken, "not a document", TestContext.Current.CancellationToken);

            var verdict = Verify(broken, setup.Parts);

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.Unreadable, verdict.Reason);
        }
        finally
        {
            setup.Dispose();
        }
    }

    private static AssemblyVerdict Verify(string output, IReadOnlyList<AssemblyPart> parts) =>
        AssemblyVerifier.Verify(new AssemblyVerificationRequest(
            output,
            AssemblyFixture.Expectations(parts),
            AssemblyFixture.Frame,
            AssemblyFixture.Contract()));

    private static async Task<Setup> SetupAsync(int parts, int pagesEach)
    {
        var directory = AssemblyFixture.NewDirectory();
        var created = Enumerable.Range(0, parts)
            .Select(index => AssemblyFixture.CreatePart(directory, index, pagesEach))
            .ToArray();
        var output = Path.Combine(directory, "assembled.pdf");
        await AssemblyFixture.AssembleAsync(created, directory, output, batchSize: 1);
        return new Setup(directory, created, output);
    }

    private static string Swap(string source, string target)
    {
        using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        for (var index = 0; index < input.PageCount; index++)
        {
            var page = index == 0 ? input.Pages[1] : index == 1 ? input.Pages[0] : input.Pages[index];
            output.AddPage(page);
        }

        output.Save(target);
        return target;
    }

    private static string BlankFirstPage(string source, string target)
    {
        using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        for (var index = 0; index < input.PageCount; index++)
        {
            if (index == 0)
            {
                var blank = output.AddPage();
                blank.Width = XUnit.FromPoint(AssemblyFixture.Frame.WidthPt);
                blank.Height = XUnit.FromPoint(AssemblyFixture.Frame.HeightPt);
                continue;
            }

            output.AddPage(input.Pages[index]);
        }

        output.Save(target);
        return target;
    }

    private sealed record Setup(string Directory, IReadOnlyList<AssemblyPart> Parts, string Output) : IDisposable
    {
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
