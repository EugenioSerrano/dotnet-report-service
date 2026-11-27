using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ReportService.Jobs;
using ReportService.ServiceDefaults;

namespace ReportService.Workers.Pdf.Capacity;

/// <summary>
/// The deployment's document capacity, registered once per process: the settings are
/// bound and checked at start, and the capacity they describe is the one every part of
/// the process reads. The work that needs it - the render and the take - each ask for it,
/// in any order, and the settings are still bound and checked exactly once.
/// </summary>
internal static class DocumentCapacityRegistration
{
    /// <summary>Registers the document capacity, unless this process already did.</summary>
    /// <param name="builder">The process being built.</param>
    public static IHostApplicationBuilder AddDocumentCapacity(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // The guard is the binding itself, never the capacity: a process that registers
        // its own capacity still has its settings bound and checked.
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(DocumentCapacityBinding)))
        {
            return builder;
        }

        services.AddSingleton<DocumentCapacityBinding>();
        builder.AddValidatedConfiguration<DocumentCapacitySettings>(DocumentCapacitySettings.SectionPath);
        services.TryAddSingleton(static provider =>
        {
            var settings = provider.GetRequiredService<IOptions<DocumentCapacitySettings>>().Value;
            return DocumentCapacity.From(settings.MaximumPages, settings.MaximumPartBytes, settings.ProfileName);
        });
        return builder;
    }

    /// <summary>Marks a service collection whose document capacity settings are bound.</summary>
    private sealed class DocumentCapacityBinding;
}
