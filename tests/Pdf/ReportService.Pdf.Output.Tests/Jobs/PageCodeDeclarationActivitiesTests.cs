using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The page-code declaration read of a PDF output, in the engine's own host for one attempt: it
/// answers what the pinned version's manifest declares, and a job never pinned is refused. The
/// cases moved here with the step they test.
/// </summary>
public sealed class PageCodeDeclarationActivitiesTests
{
    private const string Template = "invoice-energy";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThePageCodeDeclarationIsReadFromThePinnedVersionsManifest(bool declared)
    {
        using var rig = await Rig.CreateAsync("1.1.0");
        var record = TemplateVersionRecords.Sample(templateId: Template, version: "1.1.0", owner: Rig.Consumer);
        rig.Versions.Seed(record with
        {
            Manifest = record.Manifest with
            {
                Outputs = new TemplateOutputs
                {
                    Pdf = new TemplatePdfOutput
                    {
                        PageCode = declared ? new TemplatePageCode { Generator = "reference-grid" } : null,
                    },
                },
            },
        });
        await rig.PrepareAsync();

        var result = await new ActivityEnvironment().RunAsync(() =>
            rig.PageCodes.ReadPageCodeDeclarationAsync(new PageCodeDeclarationInput(rig.ReportJobId)));

        Assert.Equal(declared, result.DeclaresPageCode);
    }

    [Fact]
    public async Task AnUnpinnedJobHasNoPageCodeDeclarationToRead()
    {
        using var rig = await Rig.CreateAsync("1.1.0");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => new ActivityEnvironment().RunAsync(() =>
            rig.PageCodes.ReadPageCodeDeclarationAsync(new PageCodeDeclarationInput(rig.ReportJobId))));

        Assert.Equal("EXECUTION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
    }
}
