using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Resolution;

namespace ReportService.TemplateSource.Tests.Resolution.Seam;

/// <summary>
/// The take seam's two states and its double: a record, or one refusal with the
/// take's code, text and retryable flag unchanged. The double answers each of the
/// four refusals the resolution must be able to read, and logs the call it
/// answered.
/// </summary>
public sealed class TakeAttemptTests
{
    private static readonly SourceCommit Commit = new("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", "b2c3d4e5f60718293a4b5c6d7e8f90123456789a");

    [Fact]
    public void ATakenAttemptCarriesTheRecord()
    {
        var record = TemplateVersionRecords.Sample();

        var attempt = TakeAttempt.Taken(record);

        var taken = Assert.IsType<TakenAttempt>(attempt);
        Assert.Same(record, taken.Record);
    }

    [Fact]
    public void ARefusedAttemptCarriesTheCodeTheReasonAndTheFlagUnchanged()
    {
        var attempt = TakeAttempt.Refused("UNSUPPORTED_CAPABILITY", "The version declares an engine this deployment does not run.", retryable: false);

        var refused = Assert.IsType<RefusedAttempt>(attempt);
        Assert.Equal("UNSUPPORTED_CAPABILITY", refused.Code);
        Assert.Equal("The version declares an engine this deployment does not run.", refused.Reason);
        Assert.False(refused.Retryable);
    }

    [Fact]
    public void ATakenAttemptRefusesNoRecord()
    {
        Assert.Throws<ArgumentNullException>(() => TakeAttempt.Taken(null!));
    }

    [Theory]
    [InlineData("TEMPLATE_INVALID")]
    [InlineData("UNSUPPORTED_CAPABILITY")]
    [InlineData("TEMPLATE_NOT_FOUND")]
    [InlineData("TEMPLATE_SOURCE_UNAVAILABLE")]
    public async Task TheDoubleAnswersEveryRefusalCodeWithItsFlag(string code)
    {
        var take = new InMemoryTemplateTake();
        take.SeedRefused("invoice-energy", "1.1.0", code, "what to fix", retryable: code == "TEMPLATE_SOURCE_UNAVAILABLE");

        var attempt = await take.TakeAsync(
            "invoice-energy", "1.1.0", "billing-system", Commit, "job-0001", TestContext.Current.CancellationToken);

        var refused = Assert.IsType<RefusedAttempt>(attempt);
        Assert.Equal(code, refused.Code);
        Assert.Equal(code == "TEMPLATE_SOURCE_UNAVAILABLE", refused.Retryable);
    }

    [Fact]
    public async Task TheDoubleLogsTheCallItAnswered()
    {
        var take = new InMemoryTemplateTake();
        take.SeedTaken("invoice-energy", "1.1.0", TemplateVersionRecords.Sample());

        await take.TakeAsync("invoice-energy", "1.1.0", "billing-system", Commit, "job-0001", TestContext.Current.CancellationToken);

        var call = Assert.Single(take.Calls);
        Assert.Equal("invoice-energy", call.TemplateId);
        Assert.Equal("1.1.0", call.Version);
        Assert.Equal("billing-system", call.RequestingConsumerId);
        Assert.Equal(Commit.CommitSha, call.CommitSha);
        Assert.Equal("job-0001", call.ReportJobId);
    }

    [Fact]
    public async Task TheDoubleHonoursACancelledToken()
    {
        var take = new InMemoryTemplateTake();
        take.SeedTaken("invoice-energy", "1.1.0", TemplateVersionRecords.Sample());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => take.TakeAsync("invoice-energy", "1.1.0", "billing-system", Commit, null, cancelled.Token));
    }
}
