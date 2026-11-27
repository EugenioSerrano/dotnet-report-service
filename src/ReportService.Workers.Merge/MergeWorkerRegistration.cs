using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ReportService.Jobs;
using ReportService.Pdf.Assembly;
using ReportService.Pdf.PageCodes.Hosting;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Workers.Merge.Assembly;

namespace ReportService.Workers.Merge;

/// <summary>
/// What the merge worker registers: the one assembly step on the merge queue, the
/// stores and the taken-package reader it reads, the product assembler, and the
/// process's instruments. The queue runs one activity at a time, as the
/// architecture requires of a merge process.
/// </summary>
public static class MergeWorkerRegistration
{
    /// <summary>Registers the assembly's work and everything it runs on.</summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder AddMergeWorkerWork(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        services.TryAddSingleton(TimeProvider.System);
        builder.AddValidatedConfiguration<DocumentCapacitySettings>(DocumentCapacitySettings.SectionPath);
        services.TryAddSingleton(static provider =>
        {
            var settings = provider.GetRequiredService<IOptions<DocumentCapacitySettings>>().Value;
            return DocumentCapacity.From(settings.MaximumPages, settings.MaximumPartBytes, settings.ProfileName);
        });
        services.TryAddSingleton<IJobStore, PostgresJobStore>();
        services.TryAddSingleton<ITemplateVersionStore, PostgresTemplateVersionStore>();
        services.TryAddSingleton<TakenTemplatePackageReader>();
        services.TryAddSingleton<IPdfAssembler, PdfSharpAssembler>();
        services.AddPageCodeGenerators(builder.Configuration);
        services.TryAddSingleton(services => new AssemblyJobMetrics(
            MeterIdentity.ForHost(TelemetryDefaults.MeterName)));

        // The step is resolved with the process's own dependencies and then given the
        // interruption seam, which only the composition can install and only when the
        // process is told to interrupt. A normal process holds none. The seam's
        // one-shot marker lives under the shared storage root the worker already
        // mounts, so a resumed process sees it.
        services.AddSingleton(provider =>
        {
            var activity = ActivatorUtilities.CreateInstance<AssemblePdfActivity>(provider);
            activity.Interruption = AssemblyInterruption.FromEnvironment(
                builder.Configuration["Storage:Filesystem:Root"]);
            return activity;
        });

        var queue = AssemblyStepOptions.ServedQueue(ServedClassOptions.Read(builder));

        // The schedule-to-start latency of every assembly attempt, recorded once per attempt.
        services.TryAddSingleton(static _ => new ScheduleToStartRecorder(
            MeterIdentity.ForHost(TelemetryDefaults.MeterName),
            "merge",
            static taskQueue => JobWireValues.ToWire(PriorityTaskQueues.ClassOf(taskQueue))));
        services.AddSingleton(provider => new TaskQueueWork(
            queue,
            options =>
            {
                options.MaxConcurrentActivities = 1;
                options.AddAllActivities(provider.GetRequiredService<AssemblePdfActivity>());
                options.Interceptors = [.. options.Interceptors ?? [], new ScheduleToStartInterceptor(provider.GetRequiredService<ScheduleToStartRecorder>())];
            }));

        return builder;
    }
}
