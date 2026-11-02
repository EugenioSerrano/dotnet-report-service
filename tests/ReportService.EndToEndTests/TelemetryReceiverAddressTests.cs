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
    public void TheContainersDestinationIsReadBackFromTheReceiverEntryAndNeverResolvedAgain()
    {
        // A documentation address: no name resolution on this machine can produce it, so a
        // destination that carries it was built from the value the receiver was given.
        const string ReceiverEntry = "http://203.0.113.7:19701";
        IPAddress configured = TestComposition.ConfiguredReceiverAddress(ReceiverEntry);
        var destination = TestComposition.ContainerReceiver(configured, 43210);

        Assert.Equal(IPAddress.Parse("203.0.113.7"), configured);
        Assert.Equal("203.0.113.7", destination.Host);
        Assert.Equal(43210, destination.Port);
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
