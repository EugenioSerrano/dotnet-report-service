using ReportService.Pdf.Verification;
using ReportService.Pdf.Verification.PageCodes;
using ReportService.Storage;
using ReportService.TemplatePackages.Manifest;
using ReportService.Workers.Merge.Assembly;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Merge.Tests;

/// <summary>
/// The assembly step with a per-page code: it builds the code from the taken version's
/// declaration and the deployment's registered generator, draws it on every page of a
/// multi-part output beside the number, draws it alone on a one-part output whose
/// package declares no numbering contract, and refuses a declaration whose generator
/// this deployment no longer registers.
/// </summary>
public sealed class AssemblePdfPageCodeTests
{
    private static readonly TemplatePageCode Declared = new()
    {
        Generator = ReferenceGridPageCodeGenerator.GeneratorName,
        Area = new TemplateNumberingArea { LeftMm = 160, TopMm = 100, WidthMm = 20, HeightMm = 20 },
        Source = new TemplatePageCodeSource
        {
            Value = "{template.id}|{page.number}/{page.total}|{match}",
            Match = new TemplatePageCodeMatch { Pattern = "block[0-9]+page[0-9]+", MaxLength = 20 },
        },
    };

    [Fact]
    public async Task AMultiPartOutputCarriesItsCodeOnEveryPage()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 2, pageCode: Declared);

        var result = await RunAsync(harness, Registry());

        Assert.Equal(4, result.PageCount);
        var pages = await ReadAsync(harness, result);
        string[] values = ["block0page1", "block0page2", "block1page1", "block1page2"];
        for (var index = 0; index < 4; index++)
        {
            var expected = Content($"merge-template|{index + 1}/4|{values[index]}");
            Assert.Contains(pages[index].Graphics!.Forms, form => form.Content.AsSpan().SequenceEqual(expected));
        }
    }

    [Fact]
    public async Task AOnePartOutputWithoutANumberingContractCarriesTheCodeAlone()
    {
        using var harness = new MergeStepHarness(blocks: 1, pagesEach: 2, withNumbering: false, pageCode: Declared);

        var result = await RunAsync(harness, Registry());

        var pages = await ReadAsync(harness, result);
        Assert.Equal(2, pages.Count);
        Assert.Contains(pages[1].Graphics!.Forms, form => form.Content.AsSpan().SequenceEqual(Content("merge-template|2/2|block0page2")));
        Assert.DoesNotContain(pages[0].Page.Words, word => word.Text == "Page");
    }

    [Fact]
    public async Task AOnePartOutputWithoutACodeOrANumberingContractIsRefused()
    {
        using var harness = new MergeStepHarness(blocks: 1, pagesEach: 1, withNumbering: false);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => RunAsync(harness, Registry()));

        Assert.Equal(AssemblePdfActivity.TemplateInvalid, failure.ErrorType);
    }

    [Fact]
    public async Task AGeneratorThisDeploymentNoLongerRegistersIsRefused()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 1, pageCode: Declared);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => RunAsync(harness, pageCodes: null));

        Assert.Equal(AssemblePdfActivity.TemplateInvalid, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("not registered", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnusableDeclarationIsRefused()
    {
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 1, pageCode: Declared with { Generator = "Not A Name" });

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => RunAsync(harness, Registry()));

        Assert.Equal(AssemblePdfActivity.TemplateInvalid, failure.ErrorType);
    }

    [Fact]
    public async Task APageWithoutAValueFailsTheOutput()
    {
        var unmatched = Declared with
        {
            Source = Declared.Source! with { Match = new TemplatePageCodeMatch { Pattern = "INV-[0-9]{8}", MaxLength = 12 } },
        };
        using var harness = new MergeStepHarness(blocks: 2, pagesEach: 1, pageCode: unmatched);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => RunAsync(harness, Registry()));

        Assert.Equal(AssemblePdfActivity.ArtifactIntegrityFailed, failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }

    private static Task<AssemblePdfResult> RunAsync(MergeStepHarness harness, PageCodeGeneratorRegistry? pageCodes) =>
        new ActivityEnvironment().RunAsync(() =>
            harness.Activity(pageCodes: pageCodes).RunAsync(new AssemblePdfInput(MergeStepHarness.JobId, MergeStepHarness.OutputId)));

    private static PageCodeGeneratorRegistry Registry()
    {
        var generator = new ReferenceGridPageCodeGenerator();
        return new([new RegisteredPageCodeGenerator(generator, PageCodeGeneratorDescriptor.Of(generator, PageCodeGeneratorOrigins.Reference))]);
    }

    private static byte[] Content(string value) =>
        new ReferenceGridPageCodeGenerator()
            .Generate(value, new Dictionary<string, string>(), new PageCodeRectangle(160, 100, 20, 20).Size).Content!;

    private static async Task<IReadOnlyList<DocumentPage>> ReadAsync(MergeStepHarness harness, AssemblePdfResult result)
    {
        var read = await harness.Store.OpenReadAsync(
            ObjectKey.Create(result.ObjectKey), result.SizeBytes, result.Sha256, TestContext.Current.CancellationToken);
        var path = Path.Combine(harness.Root, "read-" + Guid.NewGuid().ToString("N") + ".pdf");
        await using (var verified = read.Value!)
        await using (var file = File.Create(path))
        {
            await verified.Content.CopyToAsync(file, TestContext.Current.CancellationToken);
        }

        return PdfWordReader.ReadDocument(path, PageFrame.FromMillimetres(210, 297, 12, 10, 12, 10).Margins).Pages;
    }
}
