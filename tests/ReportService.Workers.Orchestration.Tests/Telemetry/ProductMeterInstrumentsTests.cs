using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Workers.Orchestration.Telemetry;

namespace ReportService.Workers.Orchestration.Tests.Telemetry;

/// <summary>
/// The worker's product meter as a listener subscribes to it: the closed table of the
/// instruments it declares, each with the name, the unit and the kind it has always had,
/// and no eighth. The factory under test is given an identity nothing else in the process
/// uses, so the table the case reads is its own and not a neighbour's.
/// </summary>
public sealed class ProductMeterInstrumentsTests
{
    private static readonly MeterIdentity Identity = MeterIdentity.ForHost("ReportService.Tests.ProductMeter");

    [Fact]
    public void TheWorkerMeterDeclaresItsSevenInstrumentsAndNoEighth()
    {
        List<(string Name, string Unit, string Kind)> declared = [];
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (string.Equals(instrument.Meter.Name, Identity.Name, StringComparison.Ordinal))
                {
                    declared.Add((instrument.Name, instrument.Unit ?? string.Empty, KindOf(instrument)));
                }
            },
        };
        listener.Start();

        using var metrics = new OrchestrationMetrics(Identity);

        var expected = new (string Name, string Unit, string Kind)[]
        {
            (OrchestrationMetrics.OutcomesInstrumentName, "{upload}", "Counter"),
            (OrchestrationMetrics.VerificationDurationInstrumentName, "s", "Histogram"),
            (OrchestrationMetrics.DispatchAttemptsInstrumentName, "{attempt}", "Counter"),
            (OrchestrationMetrics.PendingAgeInstrumentName, "s", "ObservableGauge"),
            (OrchestrationMetrics.JobOutcomesInstrumentName, "{job}", "Counter"),
            (OrchestrationMetrics.InputWaitOutcomesInstrumentName, "{wait}", "Counter"),
            (OrchestrationMetrics.InputWaitNotificationsInstrumentName, "{notification}", "Counter"),
        };
        Assert.Equal(
            expected.OrderBy(row => row.Name, StringComparer.Ordinal),
            declared.OrderBy(row => row.Name, StringComparer.Ordinal));
    }

    /// <summary>The kind of instrument an exporter subscribes to, named the way the capture names it.</summary>
    private static string KindOf(Instrument instrument) => instrument switch
    {
        Counter<long> => "Counter",
        Histogram<double> => "Histogram",
        ObservableGauge<double> => "ObservableGauge",
        _ => instrument.GetType().Name,
    };
}
