using System.Diagnostics;
using ReportService.Testing;

namespace ReportService.Api.Tests;

/// <summary>The shared wait's attempt bound, its failure message and the cancellations it must not reclassify.</summary>
public sealed class WaitTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task AStalledAttemptEndsAtItsOwnBoundAndNotAtTheDeadline()
    {
        var attempts = 0;
        var elapsed = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.UntilAsync(
                "the server",
                async token =>
                {
                    attempts++;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return true;
                },
                TimeSpan.FromSeconds(2),
                PollInterval,
                TimeSpan.FromMilliseconds(300),
                CancellationToken.None));

        elapsed.Stop();
        Assert.Contains("was not ready after", failure.Message, StringComparison.Ordinal);
        Assert.Contains("(deadline 2 s)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Last error: the attempt did not answer within", failure.Message, StringComparison.Ordinal);
        Assert.True(attempts >= 3, $"the wait made {attempts} attempt(s), so an attempt was not cut at its bound");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"the wait took {elapsed.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task AnAttemptThatAnswersEndsTheWaitWithoutAnError()
    {
        var attempts = 0;

        await Wait.UntilAsync(
            "the server",
            _ => Task.FromResult(++attempts == 3),
            TimeSpan.FromSeconds(5),
            PollInterval,
            TimeSpan.FromMilliseconds(300),
            CancellationToken.None);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task TheCallersCancellationIsNotSwallowedByTheAttemptBound()
    {
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var elapsed = Stopwatch.StartNew();

        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Wait.UntilAsync(
                "the server",
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return true;
                },
                TimeSpan.FromSeconds(5),
                PollInterval,
                TimeSpan.FromSeconds(5),
                caller.Token));

        elapsed.Stop();
        Assert.True(caller.IsCancellationRequested);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"the wait ended with {cancellation.GetType().Name} after {elapsed.Elapsed.TotalSeconds:F1} s, not when the caller cancelled");
    }

    [Fact]
    public async Task AnAttemptThatThrowsIsKeptAsTheLastError()
    {
        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.UntilAsync(
                "the server",
                _ => throw new InvalidOperationException("the probe failed"),
                TimeSpan.FromMilliseconds(300),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromSeconds(5),
                CancellationToken.None));

        Assert.Contains("was not ready after", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Last error: the probe failed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbeCancellationWithTheCallersTokenLivePropagatesThroughTheExistingMethod()
    {
        var probeCancellation = new OperationCanceledException("the probe gave up on the attempt");
        var attempts = 0;

        var caught = await Record.ExceptionAsync(() =>
            Wait.UntilAsync(
                "the server",
                _ =>
                {
                    attempts++;
                    throw probeCancellation;
                },
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.Same(probeCancellation, caught);
        Assert.Equal(1, attempts);
    }
}
