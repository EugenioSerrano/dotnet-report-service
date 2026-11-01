using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using ReportService.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// The whole local composition, started from a test: every resource it
/// declares becomes healthy, the dependency images are the pinned ones, and
/// nothing it started is left running afterwards.
/// </summary>
public sealed class CompositionSmokeTests
{
    // The first run may build the PDF base image and publish the render worker image.
    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan RemovalDeadline = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task EveryResourceBecomesHealthyAndNothingRemainsRunningAfterwards()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pins = CompositionPins.Load();
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ReportService_AppHost>(cancellationToken);

        AssertImage(appHost.Resources, "postgres", pins.Database);
        AssertImage(appHost.Resources, "cache", pins.Cache);
        AssertImage(appHost.Resources, "temporal", pins.WorkflowEngine);

        // Every resource the composition declares that can report readiness, enumerated from the model so a
        // resource added later is covered.
        var resources = appHost.Resources
            .OfType<IResourceWithWaitSupport>()
            .Where(IsStartedWithTheComposition)
            .Select(r => r.Name)
            .ToList();
        var containerNames = appHost.Resources.OfType<ContainerResource>().Select(r => r.Name).ToList();
        Assert.NotEmpty(resources);

        await using (var app = await appHost.BuildAsync(cancellationToken))
        {
            await app.StartAsync(cancellationToken);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(HealthyDeadline);
            var notHealthy = new List<string>();
            foreach (var resource in resources)
            {
                try
                {
                    await app.ResourceNotifications.WaitForResourceHealthyAsync(resource, deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    notHealthy.Add(resource);
                }
            }

            Assert.True(
                notHealthy.Count == 0,
                $"Not healthy within {HealthyDeadline.TotalMinutes:F0} min: {string.Join(", ", notHealthy)} (of {string.Join(", ", resources)}).");
        }

        // After disposal, no container of the composition may remain.
        IReadOnlyList<string> leftovers = [];
        await Wait.UntilAsync(
            "removal of the composition's containers",
            async token =>
            {
                leftovers = (await RunningContainerNamesAsync(token))
                    .Where(name => containerNames.Any(resource => name.StartsWith(resource + "-", StringComparison.Ordinal)))
                    .ToList();
                return leftovers.Count == 0;
            },
            RemovalDeadline,
            TimeSpan.FromSeconds(2),
            cancellationToken);
        Assert.Empty(leftovers);
    }

    /// <summary>
    /// Leaves out the helper resources the hosting library adds by itself, such as the one that rebuilds a
    /// project on request: they are always hidden and never started with the composition, so they never
    /// report healthy. A resource the composition declares carries neither mark.
    /// </summary>
    private static bool IsStartedWithTheComposition(IResource resource) =>
        !resource.Annotations.OfType<ExplicitStartupAnnotation>().Any()
        && !resource.Annotations.OfType<HiddenAnnotation>().Any(hidden => hidden.Behavior == HiddenBehavior.Always);

    private static void AssertImage(IResourceCollection resources, string name, PinnedImage pin)
    {
        var resource = Assert.Single(resources, r => r.Name == name);
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().Last();
        Assert.Equal(pin.Image, image.Image);
        Assert.Equal(pin.Tag, image.Tag);
    }

    private static async Task<IReadOnlyList<string>> RunningContainerNamesAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "ps", "--format", "{{.Names}}" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start docker.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker ps failed with exit code {process.ExitCode}.");
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
