using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReportService.Jobs;
using ReportService.Pdf.Assembly;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;
using ReportService.Workers.Merge;
using ReportService.Workers.Merge.Assembly;
using Temporalio.Worker;

namespace ReportService.Workers.Merge.Tests;

/// <summary>
/// The worker's registration: one engine worker on the merge queue, running the
/// assembly step, with the queue held to one assembly at a time.
/// </summary>
public sealed class RegistrationTests
{
    [Fact]
    public void TheQueueRunsTheAssemblyStepOneAtATime()
    {
        var builder = Host.CreateApplicationBuilder();
        var root = Path.Combine(Path.GetTempPath(), "merge-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
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
            var work = host.Services.GetServices<TaskQueueWork>()
                .Single(item => string.Equals(item.Queue, AssemblyStepOptions.Queue, StringComparison.Ordinal));
            var options = new TemporalWorkerOptions(work.Queue);
            work.Configure(options);

            Assert.Equal(1, options.MaxConcurrentActivities);
            Assert.Contains(options.Activities, activity => activity.Name == AssemblePdfActivity.Name);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
