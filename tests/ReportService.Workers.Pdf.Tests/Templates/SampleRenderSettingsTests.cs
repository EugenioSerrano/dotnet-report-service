using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Workers.Pdf.Templates;

namespace ReportService.Workers.Pdf.Tests.Templates;

/// <summary>
/// The sample render's settings and instruments: the deadline of one call is validated
/// at start (1 to 22 seconds, 20 by default, in the section the worker binds), and the
/// two instruments carry a fixed engine, outcome and rule - never an identifier or a value.
/// </summary>
public sealed class SampleRenderSettingsTests
{
    [Fact]
    public void TheBudgetDefaultsToTwentySeconds()
    {
        var options = new SampleRenderOptions();

        Assert.Equal(20, options.RenderBudgetSeconds);
        Assert.Equal(TimeSpan.FromSeconds(20), options.RenderBudget);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void TheShippedDefaultIsTheBudgetThePlanMeasured() =>
        Assert.Equal(20, SampleRenderOptions.MaxRenderBudgetSeconds - 2);

    [Theory]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(22, true)]
    [InlineData(0, false)]
    [InlineData(23, false)]
    public void TheBudgetIsValidatedAtStart(int seconds, bool valid)
    {
        var options = new SampleRenderOptions { RenderBudgetSeconds = seconds };

        Assert.Equal(valid, !options.Validate().Any());
    }

    [Fact]
    public void TheBudgetSectionIsTheOneTheWorkerBinds() =>
        Assert.Equal("Templates:Sample", SampleRenderOptions.SectionPath);

    [Fact]
    public void ARenderAndARefusalAreRecordedWithTheirFixedTags()
    {
        var measurements = new List<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscribed) =>
            {
                if (instrument.Meter.Name == "reportservice-test-sample")
                {
                    subscribed.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, Capture(tags))));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, Capture(tags))));
        listener.Start();

        using var metrics = new SampleRenderMetrics(MeterIdentity.ForHost("reportservice-test-sample"));
        metrics.Rendered("scriban", SampleRenderMetrics.PassedOutcome, 12.5);
        metrics.Refused("handlebars", "sample-not-printed");

        Assert.Contains(measurements, measurement =>
            measurement.Instrument == SampleRenderMetrics.RenderInstrumentName
            && measurement.Value == 12.5
            && Equals(measurement.Tags["engine"], "scriban")
            && Equals(measurement.Tags["outcome"], "pass"));
        Assert.Contains(measurements, measurement =>
            measurement.Instrument == SampleRenderMetrics.FailuresInstrumentName
            && Equals(measurement.Tags["rule"], "sample-not-printed")
            && Equals(measurement.Tags["engine"], "handlebars"));
    }

    private static Dictionary<string, object?> Capture(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var captured = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in tags)
        {
            captured[key] = value;
        }

        return captured;
    }
}
