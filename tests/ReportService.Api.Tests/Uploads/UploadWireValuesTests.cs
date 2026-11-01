using ReportService.Api.Uploads;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The text forms of an upload's values: every domain status has its lower-case
/// wire value and its upper-case stored text, every mode and the one profile have
/// theirs, a value that maps to nothing is refused rather than defaulted, and a
/// timestamp is written in UTC with exactly three fractional digits.
/// </summary>
public sealed class UploadWireValuesTests
{
    [Theory]
    [InlineData(UploadStatus.Created, "created", "CREATED")]
    [InlineData(UploadStatus.Uploading, "uploading", "UPLOADING")]
    [InlineData(UploadStatus.Verifying, "verifying", "VERIFYING")]
    [InlineData(UploadStatus.Ready, "ready", "READY")]
    [InlineData(UploadStatus.Rejected, "rejected", "REJECTED")]
    [InlineData(UploadStatus.Expired, "expired", "EXPIRED")]
    public void EveryStatusHasItsWireValueAndItsStoredText(UploadStatus status, string wire, string stored)
    {
        Assert.Equal(wire, UploadWireValues.ToWire(status));
        Assert.Equal(stored, UploadWireValues.ToStoredText(status));
        Assert.Equal(status, UploadWireValues.ParseStoredStatus(stored));
    }

    [Fact]
    public void EveryModeHasItsWireValueAndItsStoredText()
    {
        Assert.Equal("API_STREAM", UploadWireValues.ToWire(UploadMode.ApiStream));
        Assert.Equal("API_STREAM", UploadWireValues.ToStoredText(UploadMode.ApiStream));
        Assert.Equal("DIRECT_STORAGE", UploadWireValues.ToWire(UploadMode.DirectStorage));
        Assert.Equal("DIRECT_STORAGE", UploadWireValues.ToStoredText(UploadMode.DirectStorage));
        Assert.Equal(UploadMode.DirectStorage, UploadWireValues.ParseStoredMode("DIRECT_STORAGE"));
    }

    [Fact]
    public void TheProfileHasTheWireValueOfTheContractAndTheStoredTextOfTheDatabase()
    {
        Assert.Equal("header-items-v1", UploadWireValues.ToWire(DataProfile.HeaderItemsV1));
        Assert.Equal("HEADER_ITEMS_V1", UploadWireValues.ToStoredText(DataProfile.HeaderItemsV1));
        Assert.Equal(DataProfile.HeaderItemsV1, UploadWireValues.ParseStoredProfile("HEADER_ITEMS_V1"));
    }

    [Fact]
    public void AValueOutsideTheEnumerationIsRefused()
    {
        var unknown = (UploadStatus)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => UploadWireValues.ToWire(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadWireValues.ToStoredText(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadWireValues.ToWire((UploadMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadWireValues.ToWire((DataProfile)99));
    }

    [Theory]
    [InlineData("created")]
    [InlineData("")]
    [InlineData("CREATED ")]
    public void AStoredTextThatIsNotAKnownValueIsAFaultNeverADefault(string stored)
    {
        Assert.Throws<InvalidOperationException>(() => UploadWireValues.ParseStoredStatus(stored));
    }

    [Fact]
    public void AnUnknownStoredModeOrProfileIsAFault()
    {
        Assert.Throws<InvalidOperationException>(() => UploadWireValues.ParseStoredMode("SIGNED_URL"));
        Assert.Throws<InvalidOperationException>(() => UploadWireValues.ParseStoredProfile("header-items-v1"));
    }

    [Fact]
    public void ASubMillisecondValueIsTruncatedAndKeptInUtc()
    {
        var value = new DateTimeOffset(2026, 11, 1, 4, 39, 23, TimeSpan.Zero).AddTicks(1234567);

        var truncated = UploadWireValues.TruncateToMillisecond(value);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero), truncated);
        Assert.Equal(TimeSpan.Zero, truncated.Offset);
    }

    [Fact]
    public void AValueInAnotherOffsetIsTruncatedToTheSameInstantInUtc()
    {
        var value = new DateTimeOffset(2026, 11, 1, 1, 39, 23, 123, TimeSpan.FromHours(-3)).AddTicks(4567);

        var truncated = UploadWireValues.TruncateToMillisecond(value);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero), truncated);
        Assert.Equal(TimeSpan.Zero, truncated.Offset);
    }

    [Fact]
    public void ATimestampIsWrittenWithThreeFractionalDigitsAndZsuffix()
    {
        var value = new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero).AddTicks(4567);

        Assert.Equal("2026-11-01T04:39:23.123Z", UploadWireValues.FormatTimestamp(value));
    }
}
