using System.Net;
using ReportService.Api.Http;
using ReportService.Api.Uploads;
using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// When an upload ends, through the API's real pipeline with a clock the test moves:
/// an upload that is not confirmed before the end of its session expires and takes
/// no more content and no confirmation, and one whose verification outlasts its own
/// deadline expires as well and can never become a snapshot afterwards. Both answers
/// are exact at the boundary, and both are shown by the record, not only by the answer.
/// </summary>
public sealed class UploadExpiryEndpointsTests
{
    private static readonly (string ConsumerId, bool Active)[] Consumers = [("dev-consumer", true)];

    private static readonly TimeSpan Deadline = TimeSpan.FromHours(6);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUploadNotConfirmedInTimeExpiresAtTheEndOfItsSessionAndLosesItsStagingObject()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        using var sent = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        host.Clock.Advance(host.Lifetime);

        using var status = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(status));

        var record = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        Assert.Equal(UploadStatus.Expired, record!.Status);
        var staging = await host.Objects.GetInfoAsync(record.StagingKey, Cancellation);
        Assert.Equal(StorageOutcome.NotFound, staging.Outcome);
        Assert.Contains(host.Logs.Lines, line => line.Contains("expired (session)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExpiredUploadTakesNoContentAndNoConfirmation()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        host.Clock.Advance(host.Lifetime);

        using var content = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);
        using var confirm = await host.Client.SendAsync(UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), Cancellation);

        Assert.Equal(HttpStatusCode.Conflict, content.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Contains(ErrorCodes.UploadStateConflict, await UploadApiHost.ProblemAsync(content), StringComparison.Ordinal);
        Assert.Contains(UploadEndpoints.ExpiredDetail, await UploadApiHost.ProblemAsync(content), StringComparison.Ordinal);
        Assert.Contains(UploadEndpoints.ExpiredDetail, await UploadApiHost.ProblemAsync(confirm), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JustBeforeTheEndEveryOperationStillWorks()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        host.Clock.Advance(host.Lifetime - TimeSpan.FromMilliseconds(1));

        using var content = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);
        using var confirm = await host.Client.SendAsync(UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), Cancellation);

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);
        var record = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        Assert.Equal(UploadStatus.Verifying, record!.Status);
    }

    [Fact]
    public async Task TheConfirmationRecordsTheDeadlineAndTheUploadExpiresByIt()
    {
        using var host = new UploadApiHost(Consumers, sessionLifetime: "01:00:00");
        var uploadId = await host.CreateAsync();
        using var sent = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        using var confirm = await host.Client.SendAsync(UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);

        var record = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        Assert.Equal(UploadStatus.Verifying, record!.Status);
        Assert.Equal(record.CompletedAt + Deadline, record.VerificationDeadline);

        // A confirming session that ended long ago does not bound the verification.
        host.Clock.Advance(host.Lifetime);
        using var beforeDeadline = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal("verifying", await UploadApiHost.StatusOfAsync(beforeDeadline));

        host.Clock.Advance(Deadline - host.Lifetime - TimeSpan.FromMilliseconds(1));
        using var justBefore = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal("verifying", await UploadApiHost.StatusOfAsync(justBefore));

        host.Clock.Advance(TimeSpan.FromMilliseconds(2));
        using var atDeadline = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(atDeadline));
        Assert.Equal(
            UploadStatus.Expired,
            (await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation))!.Status);
        Assert.Contains(host.Logs.Lines, line => line.Contains("expired (verification-deadline)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARepeatedConfirmationOfAnUploadThatExpiredWhileVerifyingAnswersItsState()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        using var sent = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);
        using var confirmed = await host.Client.SendAsync(UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        var record = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        var declared = record!.DeclaredSha256;

        host.Clock.Advance(Deadline);
        using var expired = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(expired));
        var before = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        var intents = host.Store.Intents.Count;

        using var repeated = await host.Client.SendAsync(UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(repeated));
        var after = await host.Store.FindAsync(uploadId, "dev-consumer", Cancellation);
        Assert.Equal(before, after);
        Assert.Equal(intents, host.Store.Intents.Count);

        // A declaration that differs from the one this upload was confirmed with is refused.
        var other = new string('a', 64);
        Assert.NotEqual(other, declared, StringComparer.OrdinalIgnoreCase);
        using var different = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(uploadId, "dev-consumer", $$"""{"sha256":"{{other}}"}"""), Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Contains(ErrorCodes.UploadStateConflict, await UploadApiHost.ProblemAsync(different), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedConfirmationWithTheSameDeclarationStillAnswersTheExpiredState()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        var sha256 = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";
        using var sent = await host.Client.SendAsync(UploadApiHost.ContentRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        using var confirmed = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(uploadId, "dev-consumer", $$"""{"sha256":"{{sha256}}"}"""), Cancellation);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);

        host.Clock.Advance(Deadline);
        using var expired = await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), Cancellation);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(expired));

        using var repeated = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(uploadId, "dev-consumer", $$"""{"sha256":"{{sha256}}"}"""), Cancellation);

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal("expired", await UploadApiHost.StatusOfAsync(repeated));
    }
}
