using ReportService.Workers.Orchestration.Dispatch;

namespace ReportService.Testing;

/// <summary>
/// The behaviour every intent store must show, written once and run against both of
/// them: the in-memory double the hermetic tests use and the store of the real
/// database. Because it is the same class in both places, the double cannot quietly
/// drift away from the store it stands in for.
/// </summary>
public abstract class IntentStoreContract : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);

    /// <summary>The store under test, created by <see cref="CreateStoreAsync"/> before the first case runs.</summary>
    protected IIntentStore Store { get; private set; } = null!;

    /// <summary>Creates the store this class's cases run against.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected abstract Task<IIntentStore> CreateStoreAsync(CancellationToken cancellationToken);

    /// <summary>Records a pending request due at <paramref name="due"/>.</summary>
    /// <param name="subjectId">What the request is about.</param>
    /// <param name="due">When the first attempt may run.</param>
    /// <param name="createdAt">When the request was recorded.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>The request's identifier.</returns>
    protected abstract Task<long> EnqueueAsync(string subjectId, DateTimeOffset due, DateTimeOffset createdAt, CancellationToken cancellationToken);

    /// <summary>Releases what <see cref="CreateStoreAsync"/> created, when the store holds anything.</summary>
    /// <param name="store">The store created for the class.</param>
    protected virtual ValueTask DisposeStoreAsync(IIntentStore store) => default;

    /// <summary>The kind the cases record.</summary>
    protected const string Kind = IntentKinds.StartUploadVerification;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Store = await CreateStoreAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeStoreAsync(Store);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task OnlyRequestsThatAreDueAreClaimedInRequestOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await EnqueueAsync("upload-11111111111111111111111111111111", Now.AddMinutes(-2), Now.AddMinutes(-5), cancellationToken);
        await EnqueueAsync("upload-22222222222222222222222222222222", Now.AddMinutes(-1), Now.AddMinutes(-5), cancellationToken);
        await EnqueueAsync("upload-33333333333333333333333333333333", Now.AddMinutes(5), Now.AddMinutes(-5), cancellationToken);

        await using var claim = await Store.ClaimDueAsync(Now, batchSize: 10, cancellationToken);

        Assert.Equal(2, claim.Intents.Count);
        Assert.Equal("upload-11111111111111111111111111111111", claim.Intents[0].SubjectId);
        Assert.Equal("upload-22222222222222222222222222222222", claim.Intents[1].SubjectId);
    }

    [Fact]
    public async Task TheBatchLimitBoundsOnePoll()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await EnqueueAsync("upload-44444444444444444444444444444444", Now, Now, cancellationToken);
        await EnqueueAsync("upload-55555555555555555555555555555555", Now, Now, cancellationToken);

        await using var claim = await Store.ClaimDueAsync(Now, batchSize: 1, cancellationToken);

        Assert.Single(claim.Intents);
    }

    [Fact]
    public async Task ADispatchedRequestCarriesItsReferenceAndIsNeverClaimedAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var intentId = await EnqueueAsync("upload-66666666666666666666666666666666", Now, Now, cancellationToken);

        await using (var claim = await Store.ClaimDueAsync(Now, batchSize: 10, cancellationToken))
        {
            await claim.MarkDispatchedAsync(
                intentId,
                new ExecutionReference("upload/66666666666666666666666666666666", "run-1", "1.0.0", "policy", Now),
                Now,
                cancellationToken);
            await claim.CommitAsync(cancellationToken);
        }

        await using var second = await Store.ClaimDueAsync(Now, batchSize: 10, cancellationToken);
        Assert.DoesNotContain(second.Intents, intent => intent.IntentId == intentId);
    }

    [Fact]
    public async Task AFailedAttemptIsRecordedWithItsNextAttemptAndWaitsForIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var intentId = await EnqueueAsync("upload-77777777777777777777777777777777", Now, Now, cancellationToken);

        await using (var claim = await Store.ClaimDueAsync(Now, batchSize: 10, cancellationToken))
        {
            await claim.RecordFailureAsync(intentId, Now, Now.AddSeconds(2), cancellationToken);
            await claim.CommitAsync(cancellationToken);
        }

        await using var tooEarly = await Store.ClaimDueAsync(Now.AddSeconds(1), batchSize: 10, cancellationToken);
        Assert.DoesNotContain(tooEarly.Intents, intent => intent.IntentId == intentId);

        await using var due = await Store.ClaimDueAsync(Now.AddSeconds(2), batchSize: 10, cancellationToken);
        var claimed = Assert.Single(due.Intents, intent => intent.IntentId == intentId);
        Assert.Equal(1, claimed.Attempts);
    }

    [Fact]
    public async Task AnAbandonedPollStoresNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var intentId = await EnqueueAsync("upload-88888888888888888888888888888888", Now, Now, cancellationToken);

        await using (var claim = await Store.ClaimDueAsync(Now, batchSize: 10, cancellationToken))
        {
            await claim.MarkDispatchedAsync(
                intentId,
                new ExecutionReference("upload/88888888888888888888888888888888", "run-2", "1.0.0", "policy", Now),
                Now,
                cancellationToken);
        }

        await using var again = await Store.ClaimDueAsync(Now, batchSize: 10, cancellationToken);
        Assert.Contains(again.Intents, intent => intent.IntentId == intentId);
    }

    [Fact]
    public async Task TheOldestPendingAgeIsTheOldestWaitingRequestOrNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Null(await Store.OldestPendingAgeAsync(Now, cancellationToken));

        await EnqueueAsync("upload-99999999999999999999999999999999", Now.AddMinutes(9), Now.AddMinutes(-3), cancellationToken);
        var age = await Store.OldestPendingAgeAsync(Now, cancellationToken);

        Assert.NotNull(age);
        Assert.Equal(TimeSpan.FromMinutes(3), age!.Value);
    }
}
