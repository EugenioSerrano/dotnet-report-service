using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ReportService.Testing;

/// <summary>
/// Starts the pinned containers of the integration suite. Every container is
/// named with <see cref="NamePrefix"/>, so a run can be checked for leftovers
/// by name, and is removed by its own test class through its handle.
/// </summary>
public static class ContainerSmoke
{
    /// <summary>The prefix of every container name the integration suite creates.</summary>
    public const string NamePrefix = "report-service-it-";

    /// <summary>A builder for <paramref name="image"/>, named <c>report-service-it-&lt;role&gt;-&lt;unique&gt;</c>.</summary>
    public static ContainerBuilder Builder(PinnedImage image, string role)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var unique = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        return new ContainerBuilder(image.Reference)
            .WithName($"{NamePrefix}{role}-{unique}");
    }

    /// <summary>
    /// Starts <paramref name="container"/>; when it fails to start, the
    /// container is removed and the failure names the image reference.
    /// </summary>
    public static async Task StartAsync(IContainer container, PinnedImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            await container.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"The container for {image.Reference} did not start: {exception.Message}", exception);
        }
    }
}
