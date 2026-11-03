using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Logs;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// A unit of work puts the job identifier on every log entry and every span of
/// that unit — the scope key <c>job_id</c> and the span tag <c>job.id</c> — and
/// on nothing outside it. The identifier is an internal identifier: the grammar
/// refuses anything else.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class JobCorrelationTests
{
    private const string JobId = "synthetic-6f1d0e2c";

    [Fact]
    public void TheGrammarAcceptsOnlyAJobIdentifier()
    {
        Assert.True(JobContext.IsValid(JobId));
        Assert.True(JobContext.IsValid(new string('a', 128)));
        Assert.False(JobContext.IsValid(new string('a', 129)));
        Assert.False(JobContext.IsValid("Synthetic-Upper"));
        Assert.False(JobContext.IsValid("with space"));
        Assert.False(JobContext.IsValid("with/slash"));
        Assert.False(JobContext.IsValid(string.Empty));
        Assert.False(JobContext.IsValid(null));
        Assert.Throws<ArgumentException>(() => JobContext.Begin("Synthetic-Upper", NullLoggerFactory.Instance));
        Assert.Null(JobContext.Current);
    }

    [Fact]
    public void NestingRestoresTheOuterIdentifier()
    {
        using (JobContext.Begin("outer", NullLoggerFactory.Instance))
        {
            Assert.Equal("outer", JobContext.Current);
            using (JobContext.Begin("inner", NullLoggerFactory.Instance))
            {
                Assert.Equal("inner", JobContext.Current);
            }

            Assert.Equal("outer", JobContext.Current);
        }

        Assert.Null(JobContext.Current);
    }

    [Fact]
    public async Task EveryLogEntryAndSpanOfTheUnitCarriesTheIdentifierAndNothingOutsideDoes()
    {
        await using var host = await TelemetryTestHost.StartAsync();
        using var unit = host.BeginUnit();
        var logger = host.Loggers.CreateLogger("ReportService.Unit");
        using var source = new ActivitySource(TelemetryDefaults.ActivitySourceName);

        logger.LogInformation("before the unit");
        using (JobContext.Begin(JobId, host.Loggers))
        {
            logger.LogInformation("inside the unit");
            using var span = source.StartActivity("unit");
            using (var child = source.StartActivity("child"))
            {
                logger.LogInformation("inside the child");
            }
        }

        logger.LogInformation("after the unit");

        var entries = host.Logs.Where(record => record.CategoryName == "ReportService.Unit").ToList();
        Assert.Equal(4, entries.Count);
        Assert.False(Carries(entries[0], JobContext.ScopeKey));
        Assert.True(Carries(entries[1], JobContext.ScopeKey));
        Assert.True(Carries(entries[2], JobContext.ScopeKey));
        Assert.False(Carries(entries[3], JobContext.ScopeKey));

        var spans = host.Activities.Where(activity => activity.Source.Name == TelemetryDefaults.ActivitySourceName).ToList();
        Assert.Equal(2, spans.Count);
        Assert.All(spans, span => Assert.Equal(JobId, span.GetTagItem(JobContext.ActivityTagName)));

        host.CollectMetrics();
        Assert.DoesNotContain(JobContext.ScopeKey, entries[0].Attributes?.Select(attribute => attribute.Key) ?? []);
    }

    [Fact]
    public async Task AUnitOfWorkOpenedOnADisposedFactoryOpensNoScope()
    {
        // A host that is already shutting down still holds its factory: opening a unit of
        // work on it must not escape as a fault, and the entries written in that window
        // carry no identifier.
        var host = await TelemetryTestHost.StartAsync();
        var loggers = host.Loggers;
        await host.DisposeAsync();

        using (JobContext.Begin(JobId, loggers))
        {
            Assert.Equal(JobId, JobContext.Current);
        }

        Assert.Null(JobContext.Current);
    }

    [Fact]
    public async Task ASpanStartedOutsideTheUnitCarriesNoIdentifier()
    {
        await using var host = await TelemetryTestHost.StartAsync();

        // The capture's unit is opened, so the span is exported at all; the job's unit of
        // work is the one left unopened, which is what the case is about.
        using var unit = host.BeginUnit();
        using var source = new ActivitySource(TelemetryDefaults.ActivitySourceName);

        using (source.StartActivity("outside"))
        {
        }

        var span = Assert.Single(host.Activities);
        Assert.Null(span.GetTagItem(JobContext.ActivityTagName));
    }

    private static bool Carries(LogRecord record, string key)
    {
        if (record.Attributes is { } attributes && attributes.Any(attribute => attribute.Key == key))
        {
            return true;
        }

        var found = false;
        record.ForEachScope(
            (scope, _) =>
            {
                if (scope.Scope is IEnumerable<KeyValuePair<string, object?>> pairs
                    && pairs.Any(pair => string.Equals(pair.Key, key, StringComparison.Ordinal)))
                {
                    found = true;
                }
            },
            0);
        return found;
    }
}
