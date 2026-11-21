using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;

namespace ReportService.Api.Tests.WorkflowEngine;

/// <summary>The engine settings: an address as host:port and a namespace, both required.</summary>
public sealed class WorkflowEngineOptionsTests
{
    [Theory]
    [InlineData("localhost:7233")]
    [InlineData("host.docker.internal:32771")]
    [InlineData("temporal:7233")]
    [InlineData("127.0.0.1:1")]
    [InlineData("engine:65535")]
    public void HostAndPortIsValid(string address)
    {
        Assert.Empty(new WorkflowEngineOptions { Address = address }.Validate());
    }

    [Theory]
    [InlineData(null, "is required")]
    [InlineData("", "is required")]
    [InlineData("  ", "is required")]
    [InlineData("localhost", "must be host:port")]
    [InlineData("localhost:", "must be host:port")]
    [InlineData(":7233", "must be host:port")]
    [InlineData("localhost:0", "must be host:port")]
    [InlineData("localhost:65536", "must be host:port")]
    [InlineData("localhost:abc", "must be host:port")]
    [InlineData("http://localhost:7233", "must be host:port")]
    [InlineData("user:token@localhost:7233", "must be host:port")]
    public void AnythingElseIsRefusedNamingTheSetting(string? address, string reason)
    {
        var problem = Assert.Single(new WorkflowEngineOptions { Address = address }.Validate());

        Assert.Equal(new ConfigurationProblem("Address", reason), problem);
    }

    [Fact]
    public void NamespaceDefaultsToDefaultAndMayNotBeEmpty()
    {
        Assert.Equal("default", new WorkflowEngineOptions().Namespace);

        var problem = Assert.Single(new WorkflowEngineOptions { Address = "localhost:7233", Namespace = " " }.Validate());

        Assert.Equal(new ConfigurationProblem("Namespace", "is required"), problem);
    }
}
