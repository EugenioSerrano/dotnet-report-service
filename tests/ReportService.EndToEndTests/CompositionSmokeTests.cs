using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using ReportService.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// The whole local composition, started from a test: every resource it
/// declares becomes healthy or, when another resource waits for it to finish,
/// finishes successfully; the dependency images are the pinned ones, and nothing
/// it started is left running afterwards.
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
        var appHost = await TestComposition.CreateAsync(cancellationToken);

        AssertImage(appHost.Resources, "postgres", pins.Database);
        AssertImage(appHost.Resources, "cache", pins.Cache);
        AssertImage(appHost.Resources, "temporal", pins.WorkflowEngine);

        // A resource another resource waits on to finish is a one-shot step: it never
        // reports a health status, so it is checked by its exit code instead (the
        // migrator).
        var finishingResources = appHost.Resources
            .SelectMany(resource => resource.Annotations.OfType<WaitAnnotation>())
            .Where(wait => wait.WaitType == WaitType.WaitForCompletion)
            .Select(wait => wait.Resource.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Every resource the composition declares that can report readiness, enumerated from the model so a
        // resource added later is covered.
        var resources = appHost.Resources
            .OfType<IResourceWithWaitSupport>()
            .Where(IsStartedWithTheComposition)
            .Select(r => r.Name)
            .Where(name => !finishingResources.Contains(name, StringComparer.Ordinal))
            .ToList();
        Assert.NotEmpty(resources);
        Assert.NotEmpty(finishingResources);

        // The containers this composition started, read from the model while every one of them
        // runs: an identity of its own, which no other class's composition shares.
        var containers = new List<(string Resource, string Id)>();

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

            foreach (var resource in finishingResources)
            {
                var completion = await app.ResourceNotifications.WaitForResourceAsync(
                    resource,
                    e => string.Equals(e.Snapshot.State?.Text, KnownResourceStates.Finished, StringComparison.Ordinal)
                        || string.Equals(e.Snapshot.State?.Text, KnownResourceStates.FailedToStart, StringComparison.Ordinal),
                    deadline.Token);
                Assert.True(
                    completion.Snapshot.ExitCode == 0,
                    $"{resource} finished with exit code {Convert.ToString(completion.Snapshot.ExitCode, CultureInfo.InvariantCulture) ?? "none"}.");
            }

            Assert.True(
                notHealthy.Count == 0,
                $"Not healthy within {HealthyDeadline.TotalMinutes:F0} min: {string.Join(", ", notHealthy)} (of {string.Join(", ", resources)}).");

            foreach (var container in appHost.Resources.OfType<ContainerResource>())
            {
                containers.Add((container.Name, await ContainerAsync(app, container.Name, cancellationToken)));
            }

            // A case with no container identities would pass the check below without reading
            // anything; the composition's own containers are the check's subject.
            Assert.NotEmpty(containers);
        }

        // After disposal, none of the containers this composition started may still run.
        IReadOnlyList<string> leftovers = [];
        await Wait.UntilAsync(
            "removal of the composition's containers",
            async token =>
            {
                var running = await RunningContainerIdsAsync(token);
                leftovers = containers
                    .Where(container => running.Contains(Short(container.Id), StringComparer.Ordinal))
                    .Select(container => $"{container.Resource} ({Short(container.Id)})")
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

    // The property the composition reports for a running container's identity.
    private const string ContainerIdProperty = "container.id";

    // The length of the shortened identity the container runtime lists by default.
    private const int ShortenedIdLength = 12;

    /// <summary>
    /// The identity of the container the model started for the named resource, read while it
    /// runs: the model's own id, never a name another composition's container can share.
    /// </summary>
    private static async Task<string> ContainerAsync(
        DistributedApplication app,
        string resource,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(HealthyDeadline);
        try
        {
            var container = await app.ResourceNotifications.WaitForResourceAsync(
                resource,
                update => update.Snapshot.Properties.Any(property => property.Name == ContainerIdProperty && property.Value is not null),
                deadline.Token);
            return Convert.ToString(
                container.Snapshot.Properties.Single(property => property.Name == ContainerIdProperty).Value,
                CultureInfo.InvariantCulture)!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The case identifies its own containers through the model's identity; a resource
            // that reports none within the class's deadline fails by name, and a name or a
            // prefix is never read in its place.
            throw new InvalidOperationException(
                $"The composition's '{resource}' container reported no '{ContainerIdProperty}' within {HealthyDeadline.TotalMinutes:F0} min, so the container this composition started cannot be identified.");
        }
    }

    /// <summary>
    /// The container runtime's shortened form of an identity. The model may report the full
    /// identity while the list of running containers carries it shortened, and the two agree
    /// on these characters.
    /// </summary>
    private static string Short(string containerId) => containerId[..Math.Min(ShortenedIdLength, containerId.Length)];

    private static async Task<IReadOnlyList<string>> RunningContainerIdsAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "ps", "--format", "{{.ID}}" })
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

        var ids = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // The comparison below reads two shortened identities against each other. A runtime
        // that listed any other length would make every comparison false and the check pass
        // without reading anything, so the length is required here: the case fails instead.
        foreach (var id in ids)
        {
            if (id.Length != ShortenedIdLength)
            {
                throw new InvalidOperationException(
                    $"The container runtime listed the identity '{id}' with {id.Length} characters where its shortened form of {ShortenedIdLength} was expected, so the containers this composition started cannot be compared with the ones still running.");
            }
        }

        return ids;
    }
}
