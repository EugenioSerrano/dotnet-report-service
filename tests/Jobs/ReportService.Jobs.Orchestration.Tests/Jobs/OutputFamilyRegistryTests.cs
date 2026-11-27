using ReportService.Data;
using ReportService.Jobs;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The registry the job workflow starts outputs through: a further output family is a sibling
/// the registry orders after the existing ones, and nothing in the job core changes to accept it.
/// Every case works on its own instances, so none of them touches the process's registry.
/// </summary>
public sealed class OutputFamilyRegistryTests
{
    [Fact]
    public void AFurtherFamilyIsStartedAfterTheExistingOnesInItsDeclaredOrder()
    {
        var registry = new OutputFamilyRegistry(new SiblingFamily(OutputFormat.Xlsx, 3), new HtmlOutputFamily(), new PdfOutputFamily());

        Assert.Equal([OutputFormat.Pdf, OutputFormat.Html, OutputFormat.Xlsx], registry.Families.Select(family => family.Format));
        Assert.IsType<PdfOutputFamily>(registry.SingleOutputOwner);
    }

    [Fact]
    public void TheDeliveredFamiliesStartPdfThenHtml()
    {
        var registry = OrchestrationWorkerRegistration.Families();

        Assert.Equal([OutputFormat.Pdf, OutputFormat.Html], registry.Families.Select(family => family.Format));
        Assert.IsType<PdfOutputFamily>(registry.SingleOutputOwner);
    }

    [Fact]
    public void ARegistryRefusesAnEmptySetADuplicateFormatADuplicateOrderAndTwoOwners()
    {
        Assert.Throws<ArgumentException>(() => new OutputFamilyRegistry());
        Assert.Throws<ArgumentException>(() => new OutputFamilyRegistry(new PdfOutputFamily(), new SiblingFamily(OutputFormat.Pdf, 3)));
        Assert.Throws<ArgumentException>(() => new OutputFamilyRegistry(new PdfOutputFamily(), new SiblingFamily(OutputFormat.Xlsx, 1)));
        Assert.Throws<ArgumentException>(() => new OutputFamilyRegistry(new PdfOutputFamily(), new SiblingFamily(OutputFormat.Xlsx, 3, ownsSingleOutputPath: true)));
        Assert.Throws<ArgumentException>(() => new OutputFamilyRegistry(new HtmlOutputFamily()));
    }

    [Fact]
    public void AHolderConfirmsAnEqualRegistryAndRefusesADifferentOne()
    {
        var holder = new OutputFamilyHolder();
        Assert.Throws<InvalidOperationException>(() => holder.Current);

        holder.Use(OrchestrationWorkerRegistration.Families());
        holder.Use(OrchestrationWorkerRegistration.Families());
        Assert.Equal(OrchestrationWorkerRegistration.Families(), holder.Current);

        var different = new OutputFamilyRegistry(new PdfOutputFamily(), new HtmlOutputFamily(), new SiblingFamily(OutputFormat.Xlsx, 3));
        Assert.Throws<InvalidOperationException>(() => holder.Use(different));
        Assert.Equal(OrchestrationWorkerRegistration.Families(), holder.Current);
    }

    [Fact]
    public async Task ManyCallersRegisteringTheSameFamiliesAtOnceAgree()
    {
        var holder = new OutputFamilyHolder();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => holder.Use(OrchestrationWorkerRegistration.Families()))));

        Assert.Equal(OrchestrationWorkerRegistration.Families(), holder.Current);
    }

    // A family the job core has never seen: it starts nothing here, because only the registry's
    // order and refusals are under test.
    private sealed class SiblingFamily(OutputFormat format, int order, bool ownsSingleOutputPath = false) : IOutputFamily
    {
        public OutputFormat Format => format;

        public int Order => order;

        public bool OwnsSingleOutputPath => ownsSingleOutputPath;

        public OutputRefusal? RefusalAtPartition(PartitionResult partition) => null;

        public Task StartOutputAsync(OutputStart start) => Task.CompletedTask;

        public string CodeOf(Exception? failure) => "EXECUTION_FAILED";
    }
}
