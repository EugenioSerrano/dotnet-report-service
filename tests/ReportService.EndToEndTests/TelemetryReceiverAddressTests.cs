using System.Net;

namespace ReportService.EndToEndTests;

/// <summary>
/// The address the composition gives the render worker's container for its
/// telemetry: the address the run's receiver is bound on, with the port the run
/// allocated to it. Both halves are asserted here without starting anything,
/// because getting the address wrong is silent — the container exports into the
/// void and the failure surfaces minutes later as missing telemetry.
/// </summary>
public sealed class TelemetryReceiverAddressTests
{
    [Fact]
    public void TheContainersDestinationUsesTheAddressTheReceiverIsBoundOn()
    {
        IPAddress address = TestComposition.Address();
        var receiver = TestComposition.RequestedReceiver(address);
        var destination = TestComposition.ContainerReceiver(address, 43210);

        Assert.Equal(address, IPAddress.Parse(receiver.Host));
        Assert.Equal(address, IPAddress.Parse(destination.Host));
        Assert.NotEqual(TestComposition.ContainerHostName, destination.Host);
    }

    [Fact]
    public void TheContainersDestinationCarriesThePortTheRunAllocated()
    {
        var destination = TestComposition.ContainerReceiver(TestComposition.Address(), 43210);

        Assert.Equal(43210, destination.Port);
        Assert.NotEqual(TestComposition.RequestedReceiverPort, destination.Port);
        Assert.Equal("http", destination.Scheme);
        Assert.Equal($"http://{TestComposition.Address()}:43210", TestComposition.Text(destination));
    }
}
