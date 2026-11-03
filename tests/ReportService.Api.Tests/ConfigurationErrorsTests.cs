using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests;

/// <summary>
/// The failure a start gives when it does not refuse as a configuration error: it reports
/// the exception that came out, in full, instead of reducing it to its type name.
/// </summary>
public sealed class ConfigurationErrorsTests
{
    [Fact]
    public void AChainWithoutAConfigurationErrorIsReportedWithEveryExceptionAndItsFrames()
    {
        var chain = Record.Exception(() => ThrowTheChain());

        var failure = Assert.Throws<Xunit.Sdk.XunitException>(() => _ = ConfigurationErrors.Find(chain));

        Assert.StartsWith("No configuration error was raised; got System.InvalidOperationException.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: outer", failure.Message, StringComparison.Ordinal);
        Assert.Contains("System.ObjectDisposedException: Cannot access a disposed object.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Object name: 'inner'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ConfigurationErrorsTests.ThrowTheChain", failure.Message, StringComparison.Ordinal);

        // A refusal wrapped in an aggregate is still the refusal, whatever else the aggregate carries.
        var refusal = new ConfigurationError("Setting", "reason");
        Assert.Same(refusal, ConfigurationErrors.Find(new AggregateException(refusal)));
        Assert.Same(
            refusal,
            ConfigurationErrors.Find(new AggregateException(new InvalidOperationException("first"), refusal)));
    }

    [Fact]
    public void AStartThatFailsOnSomethingElseReportsTheExceptionItRaised()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IHostedService, FailingStart>();
        using var host = builder.Build();
        var raised = Record.Exception(() => host.Start());

        var failure = Assert.Throws<Xunit.Sdk.XunitException>(() => _ = ConfigurationErrors.Find(raised));

        Assert.StartsWith("No configuration error was raised; got System.InvalidOperationException.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the start failed for its own reason", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ConfigurationErrorsTests.FailingStart", failure.Message, StringComparison.Ordinal);
    }

    private static void ThrowTheChain()
    {
        try
        {
            throw new ObjectDisposedException("inner");
        }
        catch (Exception inner)
        {
            throw new InvalidOperationException("outer", inner);
        }
    }

    private sealed class FailingStart : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the start failed for its own reason");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
