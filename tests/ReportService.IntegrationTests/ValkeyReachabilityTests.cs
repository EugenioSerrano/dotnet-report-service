using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Containers;
using ReportService.Testing;

namespace ReportService.IntegrationTests;

/// <summary>The pinned Valkey image starts and answers PING over its protocol. The class owns its container.</summary>
public sealed class ValkeyReachabilityTests : IAsyncLifetime
{
    private const int Port = 6379;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(1);

    private readonly PinnedImage _image = CompositionPins.Load().Cache;
    private IContainer? _container;

    public async ValueTask InitializeAsync()
    {
        _container = ContainerSmoke.Builder(_image, "valkey")
            .WithPortBinding(Port, assignRandomHostPort: true)
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
    public async Task PinnedServerAnswersPingWithPong()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var host = _container!.Hostname;
        var port = _container.GetMappedPublicPort(Port);
        string? reply = null;

        await Wait.UntilAsync(
            $"Valkey ({_image.Reference})",
            async token =>
            {
                reply = await PingAsync(host, port, token);
                return reply.Length > 0;
            },
            Deadline,
            TimeSpan.FromSeconds(1),
            cancellationToken);

        Assert.Equal("+PONG", reply);
    }

    private static async Task<string> PingAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync("PING\r\n"u8.ToArray(), cancellationToken);
        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        return Encoding.ASCII.GetString(buffer, 0, read).TrimEnd('\r', '\n');
    }
}
