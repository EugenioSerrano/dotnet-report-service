using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Workers.Pdf.Rendering;
using ReportService.Workers.Pdf.Templates;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// A render worker serves the render queue of exactly one priority class - the block, the take
/// and the by-name resolution all on it - the normal class's queue when the setting is absent.
/// </summary>
public sealed class ServedClassRegistrationTests
{
    [Theory]
    [InlineData(null, "report-pdf-rendering")]
    [InlineData("high", "report-pdf-rendering-high")]
    [InlineData("low", "report-pdf-rendering-low")]
    public void TheRenderRegistrationIsOnTheQueueOfItsClass(string? servedClass, string expectedQueue)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = "ReportService.Workers.Pdf",
        });
        if (servedClass is not null)
        {
            builder.Configuration["Priority:ServedClass"] = servedClass;
        }

        builder.AddPdfRenderingWork();
        using var host = builder.Build();

        var work = Assert.Single(host.Services.GetServices<TaskQueueWork>());
        Assert.Equal(expectedQueue, work.Queue);
    }

    [Theory]
    [InlineData("high")]
    [InlineData("normal")]
    [InlineData("low")]
    public void TheBlockTheTakeAndTheResolutionNameTheSameQueueForEachClass(string servedClass)
    {
        var block = RenderPdfBlockOptions.ServedQueue(servedClass);
        Assert.Equal(block, TakeTemplateVersionActivityOptions.ServedQueue(servedClass));
        Assert.Equal(block, TemplateResolutionActivityOptions.ServedQueue(servedClass));
        Assert.True(PriorityTaskQueues.TryParseServedClass(servedClass, out var priority));
        Assert.Equal(block, RenderPdfBlockOptions.QueueFor(priority));
        Assert.Equal(block, TakeTemplateVersionActivityOptions.QueueFor(priority));
        Assert.Equal(block, TemplateResolutionActivityOptions.QueueFor(priority));
    }

    [Fact]
    public void ARefusedServedClassIsAStartProblem() =>
        Assert.NotEmpty(ServedClassOptions.Problems("high,low", static value => PriorityTaskQueues.TryParseServedClass(value, out _)));
}
