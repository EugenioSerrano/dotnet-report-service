using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReportService.Jobs;
using ReportService.Pdf.Assembly;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;
using ReportService.Workers.Merge.Assembly;
using Temporalio.Worker;

namespace ReportService.Workers.Merge.Tests;

/// <summary>
/// A merge worker serves the merge queue of exactly one priority class: the normal class's
/// queue when the setting is absent, the class's own queue when it names one; one assembly at a
/// time either way; and every attempt's schedule-to-start latency is recorded.
/// </summary>
public sealed class ServedClassRegistrationTests
{
    [Theory]
    [InlineData(null, "report-pdf-merge")]
    [InlineData("normal", "report-pdf-merge")]
    [InlineData("high", "report-pdf-merge-high")]
    [InlineData("low", "report-pdf-merge-low")]
    public void TheWorkerServesTheQueueOfItsClassOneAssemblyAtATime(string? servedClass, string expectedQueue)
    {
        var root = Path.Combine(Path.GetTempPath(), "merge-served-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = Host.CreateApplicationBuilder();
            if (servedClass is not null)
            {
                builder.Configuration["Priority:ServedClass"] = servedClass;
            }

            builder.Services.AddSingleton<IJobStore>(new ReportService.Testing.InMemoryJobStore(new InMemoryUploadStore()));
            builder.Services.AddSingleton<ITemplateVersionStore>(new FakeVersionStore(null));
            builder.Services.AddSingleton<IObjectStore>(FilesystemObjectStore.Open(
                new FilesystemStorageOptions { Root = Path.Combine(root, "store"), CreateIfMissing = true },
                root,
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FilesystemObjectStore>.Instance));
            builder.Services.AddSingleton<IPdfAssembler, PdfSharpAssembler>();
            builder.AddMergeWorkerWork();

            using var host = builder.Build();
            var work = Assert.Single(host.Services.GetServices<TaskQueueWork>());
            Assert.Equal(expectedQueue, work.Queue);

            var options = new TemporalWorkerOptions(work.Queue);
            work.Configure(options);
            Assert.Equal(1, options.MaxConcurrentActivities);
            Assert.Contains(options.Activities, activity => activity.Name == AssemblePdfActivity.Name);
            Assert.Contains(options.Interceptors!, interceptor => interceptor is ScheduleToStartInterceptor);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("high", "report-pdf-merge-high")]
    [InlineData("low", "report-pdf-merge-low")]
    [InlineData(null, "report-pdf-merge")]
    public void TheAssemblyStepNamesTheQueueOfEachClass(string? servedClass, string expected)
    {
        Assert.Equal(expected, AssemblyStepOptions.ServedQueue(servedClass));
        Assert.Equal(PriorityTaskQueues.For(AssemblyStepOptions.Queue, JobPriority.High), AssemblyStepOptions.QueueFor(JobPriority.High));
    }
}
