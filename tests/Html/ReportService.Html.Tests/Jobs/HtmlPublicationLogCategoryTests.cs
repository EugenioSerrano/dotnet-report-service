using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Retention;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The HTML publication logs under the category it has always logged under - the publication
/// step's own - so the queries and dashboards that read its entries keep finding them now that the
/// step lives with the HTML family.
/// </summary>
public sealed class HtmlPublicationLogCategoryTests
{
    [Fact]
    public void ThePublicationLogsUnderThePublicationStepsCategory()
    {
        var loggers = new CategoryRecordingFactory();

        _ = new HtmlOutputActivities(
            new InMemoryJobStore(new InMemoryUploadStore()),
            TimeProvider.System,
            Options.Create(new RetentionOptions { Outputs = TimeSpan.FromDays(7) }),
            loggers);

        Assert.Equal(["ReportService.Workers.Orchestration.Jobs.PdfOutputActivities"], loggers.Categories);
        Assert.Equal(HtmlOutputActivities.PublicationLogCategory, typeof(PdfOutputActivities).FullName);

        // The category the publication step's typed logger asks its factory for.
        var typed = new CategoryRecordingFactory();
        _ = new Logger<PdfOutputActivities>(typed);
        Assert.Equal(typed.Categories, loggers.Categories);
    }

    private sealed class CategoryRecordingFactory : ILoggerFactory
    {
        public List<string> Categories { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return NullLogger.Instance;
        }

        public void Dispose()
        {
        }
    }
}
