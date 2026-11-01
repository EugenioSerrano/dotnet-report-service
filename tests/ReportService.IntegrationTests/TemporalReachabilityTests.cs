using System.Net;
using DotNet.Testcontainers.Containers;
using ReportService.Testing;

namespace ReportService.IntegrationTests;

/// <summary>The pinned Temporal development server starts and serves its UI. The class owns its container.</summary>
public sealed class TemporalReachabilityTests : IAsyncLifetime
{
    private const int GrpcPort = 7233;
    private const int UiPort = 8233;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    private readonly PinnedImage _image = CompositionPins.Load().WorkflowEngine;
    private IContainer? _container;

    public async ValueTask InitializeAsync()
    {
        _container = ContainerSmoke.Builder(_image, "temporal")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(GrpcPort, assignRandomHostPort: true)
            .WithPortBinding(UiPort, assignRandomHostPort: true)
            .Build();
        await ContainerSmoke.StartAsync(_container, _image, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task DevelopmentServerServesItsUserInterface()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uiAddress = new UriBuilder(Uri.UriSchemeHttp, _container!.Hostname, _container.GetMappedPublicPort(UiPort)).Uri;
        using var http = new HttpClient { BaseAddress = uiAddress, Timeout = TimeSpan.FromSeconds(10) };
        var status = HttpStatusCode.ServiceUnavailable;

        await Wait.UntilAsync(
            $"Temporal UI ({_image.Reference})",
            async token =>
            {
                using var response = await http.GetAsync(new Uri("/", UriKind.Relative), token);
                status = response.StatusCode;
                return status == HttpStatusCode.OK;
            },
            Deadline,
            TimeSpan.FromSeconds(2),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(TestcontainersStates.Running, _container.State);
    }
}
