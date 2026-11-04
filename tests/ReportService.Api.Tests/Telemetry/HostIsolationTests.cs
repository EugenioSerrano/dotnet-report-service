using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using ReportService.Api.Http;
using ReportService.Api.Telemetry;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// Two hosts alive in one process, each publishing a product instrument only it
/// created and opening a unit of work of its own: a host's capture holds what that
/// host produced and nothing a host beside it produced while both were running.
/// <para>
/// The two hosts are started before either records anything and neither is disposed
/// while the other is alive, so a case cannot pass because a neighbouring host was
/// absent: the isolation is asserted, not the schedule.
/// </para>
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class HostIsolationTests
{
    private const string FirstJobId = "synthetic-1a2b3c4d";
    private const string SecondJobId = "synthetic-5e6f7a8b";
    private const string FirstCategory = "ReportService.Isolation.First";
    private const string SecondCategory = "ReportService.Isolation.Second";

    [Fact]
    public async Task TwoHostsInOneProcessCaptureOnlyTheirOwnInstruments()
    {
        await using var first = await TelemetryTestHost.StartAsync(diagnostics: true);
        await using var second = await TelemetryTestHost.StartAsync(diagnostics: true);

        using (var acceptances = new JobAcceptanceMetrics(first.Identity))
        using (var refusals = new ConsumerRefusalMetrics(second.Identity))
        {
            acceptances.Accepted();
            refusals.Refusal(ErrorCodes.ConsumerUnknown, ConsumerRefusalMetrics.CreateOperation, "missing");
        }

        first.CollectMetrics();
        second.CollectMetrics();

        var firstInstruments = first.Capture.Take(1000).Instruments;
        var secondInstruments = second.Capture.Take(1000).Instruments;

        Assert.DoesNotContain(firstInstruments, instrument => instrument.Name == ConsumerRefusalMetrics.InstrumentName);
        Assert.DoesNotContain(secondInstruments, instrument => instrument.Name == JobAcceptanceMetrics.InstrumentName);

        // Both hosts are still running when the assertion is read.
        Assert.NotNull(first.Services.GetRequiredService<MeterProvider>());
        Assert.NotNull(second.Services.GetRequiredService<MeterProvider>());
    }

    [Fact]
    public async Task EachHostCreatesItsMeterFromItsOwnIdentity()
    {
        await using var first = await TelemetryTestHost.StartAsync(diagnostics: true);
        await using var second = await TelemetryTestHost.StartAsync(diagnostics: true);

        Assert.NotEqual(first.Identity, second.Identity);

        using (var acceptances = new JobAcceptanceMetrics(first.Identity))
        using (var refusals = new ConsumerRefusalMetrics(second.Identity))
        {
            acceptances.Accepted();
            refusals.Refusal(ErrorCodes.ConsumerUnknown, ConsumerRefusalMetrics.CreateOperation, "missing");
        }

        first.CollectMetrics();
        second.CollectMetrics();

        var firstInstruments = first.Capture.Take(1000).Instruments;
        var secondInstruments = second.Capture.Take(1000).Instruments;

        // Each host's own product instruments — the one the registration creates and the one
        // its factory was handed — are declared on that host's identity, and on no other.
        var firstProduct = firstInstruments
            .Where(instrument => instrument.Name is BuildInfoMetric.InstrumentName or JobAcceptanceMetrics.InstrumentName)
            .ToList();
        var secondProduct = secondInstruments
            .Where(instrument => instrument.Name is BuildInfoMetric.InstrumentName or ConsumerRefusalMetrics.InstrumentName)
            .ToList();

        Assert.Equal(2, firstProduct.Count);
        Assert.Equal(2, secondProduct.Count);
        Assert.All(firstProduct, instrument => Assert.Equal(first.Identity.Name, instrument.Meter));
        Assert.All(secondProduct, instrument => Assert.Equal(second.Identity.Name, instrument.Meter));
    }

    [Fact]
    public async Task AUnitOfWorkIsWrittenWithItsIdentifierOnlyOnTheHostThatOpenedIt()
    {
        await using var first = await TelemetryTestHost.StartAsync(diagnostics: true);
        await using var second = await TelemetryTestHost.StartAsync(diagnostics: true);

        Write(first, FirstCategory, FirstJobId);
        Write(second, SecondCategory, SecondJobId);

        var firstEntries = first.Logs.Where(record => record.CategoryName == FirstCategory).ToList();
        var secondEntries = second.Logs.Where(record => record.CategoryName == SecondCategory).ToList();

        Assert.Equal(2, firstEntries.Count);
        Assert.False(Carries(firstEntries[0], JobContext.ScopeKey));
        Assert.True(Carries(firstEntries[1], JobContext.ScopeKey, FirstJobId));

        Assert.Equal(2, secondEntries.Count);
        Assert.False(Carries(secondEntries[0], JobContext.ScopeKey));
        Assert.True(Carries(secondEntries[1], JobContext.ScopeKey, SecondJobId));

        Assert.DoesNotContain(first.Logs, record => Carries(record, JobContext.ScopeKey, SecondJobId));
        Assert.DoesNotContain(second.Logs, record => Carries(record, JobContext.ScopeKey, FirstJobId));
    }

    /// <summary>One entry outside a unit and one inside it, written through <paramref name="host"/>'s own loggers.</summary>
    private static void Write(TelemetryTestHost host, string category, string jobId)
    {
        var logger = host.Loggers.CreateLogger(category);
        logger.LogInformation("outside the unit");
        using (JobContext.Begin(jobId, host.Loggers))
        {
            logger.LogInformation("inside the unit");
        }
    }

    /// <summary>Whether <paramref name="record"/> carries <paramref name="key"/>, and with which value.</summary>
    private static bool Carries(LogRecord record, string key, string? value = null)
    {
        var wanted = (key, value);
        if (record.Attributes is { } attributes && attributes.Any(attribute => Matches(attribute, wanted)))
        {
            return true;
        }

        var found = false;
        record.ForEachScope(
            (scope, _) =>
            {
                if (!found
                    && scope.Scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    found = pairs.Any(pair => Matches(pair, wanted));
                }
            },
            0);
        return found;
    }

    private static bool Matches(KeyValuePair<string, object?> pair, (string Key, string? Value) wanted) =>
        string.Equals(pair.Key, wanted.Key, StringComparison.Ordinal)
        && (wanted.Value is null || string.Equals(Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture), wanted.Value, StringComparison.Ordinal));
}
