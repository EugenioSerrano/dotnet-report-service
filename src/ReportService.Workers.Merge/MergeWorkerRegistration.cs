using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.Jobs;
using ReportService.Pdf.Assembly;
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
        services.TryAddSingleton<IJobStore, PostgresJobStore>();
        services.TryAddSingleton<ITemplateVersionStore, PostgresTemplateVersionStore>();
        services.TryAddSingleton<TakenTemplatePackageReader>();
        services.TryAddSingleton<IPdfAssembler, PdfSharpAssembler>();
        services.TryAddSingleton(services => new AssemblyJobMetrics(
            MeterIdentity.ForHost(TelemetryDefaults.MeterName)));

        // The step is resolved with the process's own dependencies and then given the
        // interruption seam, which only the composition can install and only when the
        // process is told to interrupt. A normal process holds none.
        services.AddSingleton(provider =>
        {
            var activity = ActivatorUtilities.CreateInstance<AssemblePdfActivity>(provider);
            activity.Interruption = AssemblyInterruption.FromEnvironment();
            return activity;
        });

        services.AddSingleton(provider => new TaskQueueWork(
            AssemblyStepOptions.Queue,
            options =>
            {
                options.MaxConcurrentActivities = 1;
                options.AddAllActivities(provider.GetRequiredService<AssemblePdfActivity>());
            }));

        return builder;
    }
}
