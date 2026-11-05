using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Workers.Pdf.Rendering;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The instruments a printed block reports: one duration per phase, the verification's result
/// with its failure class, the pages of the block, and the requests the document made that the
/// route policy aborted. Every tag is asserted as a value from the fixed set it belongs to, and
/// the block's own identifiers never appear as a tag.
/// </summary>
public sealed class PdfRenderingMetricsTests
{
    private static readonly MeterIdentity Identity = MeterIdentity.ForHost("ReportService.Tests.PdfRenderingMetrics");

    [Fact]
    public void EachPhaseIsRecordedAsSecondsUnderItsOwnTag()
    {
        using var metrics = new PdfRenderingMetrics(Identity);
        using var recorded = new Recorded(Identity);

        metrics.BlockPhase(PdfRenderingMetrics.ComposePhase, TimeSpan.FromMilliseconds(250));
        metrics.BlockPhase(PdfRenderingMetrics.PrintPhase, TimeSpan.FromSeconds(2));
        metrics.BlockPhase(PdfRenderingMetrics.VerifyPhase, TimeSpan.FromSeconds(3));
        metrics.BlockPhase(PdfRenderingMetrics.StorePhase, TimeSpan.FromSeconds(4));

        var durations = recorded.Of(PdfRenderingMetrics.BlockDurationInstrumentName);
        Assert.Equal(4, durations.Count);
        Assert.Equal(["compose", "print", "verify", "store"], durations.Select(measurement => measurement.Tag("phase")));
        Assert.Equal([0.25, 2, 3, 4], durations.Select(measurement => measurement.Value));
    }

    [Fact]
    public void AVerificationIsRecordedWithItsResultAndClass()
    {
        using var metrics = new PdfRenderingMetrics(Identity);
        using var recorded = new Recorded(Identity);

        metrics.VerificationOutcome(passed: true, failureClass: null);
        metrics.VerificationOutcome(passed: false, failureClass: "text-dropped");

        var outcomes = recorded.Of(PdfRenderingMetrics.VerificationOutcomesInstrumentName);
        Assert.Equal(2, outcomes.Count);
        Assert.Equal(PdfRenderingMetrics.PassResult, outcomes[0].Tag("result"));
        Assert.Equal(PdfRenderingMetrics.NoClass, outcomes[0].Tag("class"));
        Assert.Equal(PdfRenderingMetrics.FailResult, outcomes[1].Tag("result"));
        Assert.Equal("text-dropped", outcomes[1].Tag("class"));
    }

    [Fact]
    public void ThePagesOfABlockAndTheRequestsItAbortedAreRecorded()
    {
        using var metrics = new PdfRenderingMetrics(Identity);
        using var recorded = new Recorded(Identity);

        metrics.Pages(3);
        metrics.AbortedRequests(2);
        metrics.AbortedRequests(0);

        var pages = Assert.Single(recorded.Of(PdfRenderingMetrics.PagesInstrumentName));
        Assert.Equal(3, pages.Value);

        // A render that aborted nothing records nothing: the counter counts requests, not renders.
        var aborted = Assert.Single(recorded.Of(PdfRenderingMetrics.AbortedRequestsInstrumentName));
        Assert.Equal(2, aborted.Value);
    }

    /// <summary>What the product's meter published while a case ran, listened to by its own identity.</summary>
    private sealed class Recorded : IDisposable
    {
        private readonly MeterListener _listener = new();

        private readonly Lock _gate = new();

        private readonly List<Measurement> _measurements = [];

        public Recorded(MeterIdentity identity)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, identity.Name, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
                Add(new Measurement(instrument.Name, value, tags.ToArray())));
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
                Add(new Measurement(instrument.Name, value, tags.ToArray())));
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Of(string instrumentName)
        {
            lock (_gate)
            {
                return [.. _measurements.Where(measurement => string.Equals(measurement.Name, instrumentName, StringComparison.Ordinal))];
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Add(Measurement measurement)
        {
            lock (_gate)
            {
                _measurements.Add(measurement);
            }
        }
    }

    /// <summary>One measurement as the meter published it.</summary>
    private sealed record Measurement(string Name, double Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        public string? Tag(string name) =>
            Tags.FirstOrDefault(tag => string.Equals(tag.Key, name, StringComparison.Ordinal)).Value as string;
    }
}
