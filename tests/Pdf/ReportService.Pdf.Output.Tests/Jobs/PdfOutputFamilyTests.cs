using ReportService.Data;
using ReportService.Jobs;
using ReportService.Workers.Orchestration.Jobs;

using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The PDF output family as the job workflow starts it: first in the start order, the owner of
/// the single-output path, the refusal the data step recorded, and its child's code mapping.
/// </summary>
public sealed class PdfOutputFamilyTests
{
    [Fact]
    public void OnlyThePdfFamilyCarriesARefusalTheDataStepRecorded()
    {
        var refused = Partition() with { PdfRefusal = new PdfOutputRefusal("ROW_BOUND_EXCEEDED", "a record is larger than the bound") };

        Assert.Equal(new OutputRefusal("ROW_BOUND_EXCEEDED", "a record is larger than the bound"), new PdfOutputFamily().RefusalAtPartition(refused));
        Assert.Null(new HtmlOutputFamily().RefusalAtPartition(refused));
        Assert.Null(new PdfOutputFamily().RefusalAtPartition(Partition()));
    }

    [Fact]
    public void CodeOfIsTheChildsOwnCodeMapping()
    {
        var family = new PdfOutputFamily();
        var coded = new ApplicationFailureException("the block could not be rendered", errorType: "RENDER_TIMEOUT", nonRetryable: true);
        var uncoded = new ApplicationFailureException("no code");

        Assert.Equal(ReportPdfOutputWorkflow.CodeOf(coded), family.CodeOf(coded));
        Assert.Equal("RENDER_TIMEOUT", family.CodeOf(coded));
        Assert.Equal(ReportPdfOutputWorkflow.CodeOf(uncoded), family.CodeOf(uncoded));
        Assert.Equal(ReportPdfOutputWorkflow.CodeOf(null), family.CodeOf(null));
    }

    [Fact]
    public void ThePdfFamilyStartsFirstAndOwnsTheSingleOutputPath()
    {
        var family = new PdfOutputFamily();

        Assert.Equal(OutputFormat.Pdf, family.Format);
        Assert.Equal(1, family.Order);
        Assert.True(family.OwnsSingleOutputPath);
        Assert.Throws<ArgumentNullException>(() => family.RefusalAtPartition(null!));
    }

    private static PartitionResult Partition() =>
        new(1, 1, 1, 24, 1, "block/job/0/attempt", 1, new string('a', 64), "index/job/0/attempt", 1, new string('b', 64));
}
