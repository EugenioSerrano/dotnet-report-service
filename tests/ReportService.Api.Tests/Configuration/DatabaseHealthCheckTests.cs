using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.Api.Tests.Configuration;

/// <summary>
/// The metadata-database health check: current is healthy, behind and ahead are
/// not, and a reader that throws or does not answer makes the database
/// unreachable — with no host, user, password or exception text in its body.
/// </summary>
public sealed class DatabaseHealthCheckTests
{
    private const string PlantedSecret = "planted-secret-9d2f07";
    private const string Baseline = "0001_baseline.sql";
    private const string Second = "0002_test-table.sql";

    private readonly LogCapture _logs = new();

    [Fact]
    public async Task ACurrentSchemaIsHealthyWithExpectedAndApplied()
    {
        var result = await Check(Reader(Baseline), Baseline).CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(Baseline, result.Data["expected"]);
        Assert.Equal(Baseline, result.Data["applied"]);
        Assert.Equal(["expected", "applied"], result.Data.Keys);
        Assert.Contains("metadata database ready at " + Baseline, Lines().Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASchemaBehindTheExpectedVersionIsNotReady()
    {
        var result = await Check(Reader(), Baseline).CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(Baseline, result.Data["expected"]);
        Assert.Equal(SchemaVersion.None, result.Data["applied"]);
        Assert.Contains("metadata database not ready: behind; expected 0001_baseline.sql, applied none", Lines().Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASchemaAheadOfTheExpectedVersionIsNotReady()
    {
        var result = await Check(Reader(Baseline, Second), Baseline).CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(Second, result.Data["applied"]);
        Assert.Contains("metadata database not ready: ahead", Lines().Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReaderThatThrowsMakesTheDatabaseUnreachable()
    {
        var reader = new FakeReader(_ => throw new InvalidOperationException("host=127.0.0.1 user=nobody " + PlantedSecret));

        var result = await Check(reader, Baseline).CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(["expected", "error"], result.Data.Keys);
        Assert.Equal(Baseline, result.Data["expected"]);
        Assert.Equal("unreachable", result.Data["error"]);
        Assert.DoesNotContain(PlantedSecret, result.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedSecret, string.Join(" ", result.Data.Values), StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", Lines().Single(), StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedSecret, Lines().Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReaderThatDoesNotAnswerWithinFiveSecondsMakesTheDatabaseUnreachable()
    {
        var neverEnding = new TaskCompletionSource<IReadOnlyList<string>>();
        var reader = new FakeReader(_ => neverEnding.Task);

        var started = DateTimeOffset.UtcNow;
        var result = await Check(reader, Baseline).CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("unreachable", result.Data["error"]);
        Assert.InRange(DateTimeOffset.UtcNow - started, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ItNeverThrowsAndNeverPutsASecretInItsDescription()
    {
        var reader = new FakeReader(_ => throw new TimeoutException("password=" + PlantedSecret));

        var result = await Check(reader, Baseline).CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Description);
        Assert.DoesNotContain(PlantedSecret, result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItLogsOnceOnEachTransitionAndOnceOnRecovery()
    {
        var applied = Array.Empty<string>();
        var check = Check(new FakeReader(_ => Task.FromResult<IReadOnlyList<string>>(applied)), Baseline);

        await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);
        await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);
        Assert.Single(NotReadyLines());

        applied = [Baseline];
        await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);
        await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);
        Assert.Single(ReadyLines());

        applied = [];
        await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);
        applied = [Baseline];
        await check.CheckHealthAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(2, NotReadyLines().Count);
        Assert.Equal(2, ReadyLines().Count);
    }

    private List<string> NotReadyLines() =>
        [.. Lines().Where(line => line.Contains("metadata database not ready: behind", StringComparison.Ordinal))];

    private List<string> ReadyLines() =>
        [.. Lines().Where(line => line.Contains("metadata database ready at " + Baseline, StringComparison.Ordinal))];

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration("metadata-database", new PassingCheck(), null, null),
    };

    private sealed class PassingCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Healthy());
    }

    private static FakeReader Reader(params string[] names) => new(_ => Task.FromResult<IReadOnlyList<string>>(names));

    private DatabaseHealthCheck Check(ISchemaJournalReader reader, string expected) =>
        new(reader, Options.Create(new DatabaseOptions { ConnectionString = "Host=127.0.0.1;Database=nothing", ExpectedSchemaVersion = expected }), new LoggerFactory([_logs]).CreateLogger<DatabaseHealthCheck>());

    private IReadOnlyList<string> Lines() => [.. _logs.Lines];

    private sealed class FakeReader(Func<CancellationToken, Task<IReadOnlyList<string>>> read) : ISchemaJournalReader
    {
        public Task<IReadOnlyList<string>> ReadAppliedScriptsAsync(CancellationToken cancellationToken) => read(cancellationToken);
    }
}
