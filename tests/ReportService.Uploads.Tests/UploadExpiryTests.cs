using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.Uploads.Tests;

/// <summary>
/// The one rule that decides when an upload may no longer be used: at its session's
/// end while it was never confirmed, and at its verification deadline once it was.
/// The cases sit on both sides of each boundary, because the answer at the instant
/// itself is what tells "before" from "not before".
/// </summary>
public sealed class UploadExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    private static readonly DateTimeOffset ExpiresAt = Now.AddHours(1);

    private static readonly DateTimeOffset Deadline = Now.AddHours(6);

    [Theory]
    [InlineData(UploadStatus.Created)]
    [InlineData(UploadStatus.Uploading)]
    public void AnUnconfirmedUploadIsOverdueAtItsSessionEndAndNotBeforeIt(UploadStatus status)
    {
        var upload = Upload(status);

        Assert.False(UploadExpiry.IsOverdue(upload, ExpiresAt.AddMilliseconds(-1)));
        Assert.True(UploadExpiry.IsOverdue(upload, ExpiresAt));
        Assert.True(UploadExpiry.IsOverdue(upload, ExpiresAt.AddDays(1)));
    }

    [Fact]
    public void AConfirmedUploadIsOverdueAtItsDeadlineAndNotBeforeIt()
    {
        var upload = Upload(UploadStatus.Verifying);

        Assert.False(UploadExpiry.IsOverdue(upload, Deadline.AddMilliseconds(-1)));
        Assert.True(UploadExpiry.IsOverdue(upload, Deadline));
    }

    [Fact]
    public void AConfirmedUploadIsNotOverdueAtTheEndOfItsSession()
    {
        var upload = Upload(UploadStatus.Verifying);

        // The session's end bounds the confirmation, never the verification.
        Assert.False(UploadExpiry.IsOverdue(upload, ExpiresAt));
        Assert.False(UploadExpiry.IsOverdue(upload, ExpiresAt.AddHours(1)));
    }

    [Fact]
    public void AConfirmedUploadWithoutADeadlineIsNeverOverdue()
    {
        var upload = new UploadRecord(
            "upload-0123456789abcdef0123456789abcdef",
            "dev-consumer",
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Verifying,
            ObjectKey.FromSegments("staging", "upload-0123456789abcdef0123456789abcdef", "content"),
            Now,
            ExpiresAt)
        {
            CompletedAt = Now,
            VerificationDeadline = null,
        };

        Assert.False(UploadExpiry.IsOverdue(upload, Deadline.AddYears(1)));
    }

    [Theory]
    [InlineData(UploadStatus.Ready)]
    [InlineData(UploadStatus.Rejected)]
    [InlineData(UploadStatus.Expired)]
    public void AnUploadThatAlreadyEndedIsNeverOverdue(UploadStatus status)
    {
        var upload = Upload(status);

        Assert.False(UploadExpiry.IsOverdue(upload, ExpiresAt.AddDays(30)));
    }

    [Theory]
    [InlineData(UploadStatus.Created, UploadExpiry.SessionCause)]
    [InlineData(UploadStatus.Uploading, UploadExpiry.SessionCause)]
    [InlineData(UploadStatus.Verifying, UploadExpiry.VerificationDeadlineCause)]
    public void TheCauseNamesWhichEndTheUploadRanOutOf(UploadStatus status, string cause) =>
        Assert.Equal(cause, UploadExpiry.CauseOf(status));

    private static UploadRecord Upload(UploadStatus status, DateTimeOffset? deadline = null) => new(
        "upload-0123456789abcdef0123456789abcdef",
        "dev-consumer",
        UploadMode.ApiStream,
        DataProfile.HeaderItemsV1,
        status,
        ObjectKey.FromSegments("staging", "upload-0123456789abcdef0123456789abcdef", "content"),
        Now,
        ExpiresAt)
    {
        CompletedAt = status is UploadStatus.Verifying or UploadStatus.Ready ? Now : null,
        VerificationDeadline = deadline ?? (status is UploadStatus.Verifying or UploadStatus.Ready ? Deadline : null),
    };
}
