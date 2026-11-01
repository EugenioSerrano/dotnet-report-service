using ReportService.Api.Uploads;
using ReportService.Storage;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The identifier of an upload session: lower-case hexadecimal after a fixed
/// prefix, one segment of a storage key, and never the same twice.
/// </summary>
public sealed class UploadIdentifierTests
{
    [Fact]
    public void AnIdentifierHasThePrefixAndThirtyTwoLowerHexCharacters()
    {
        var uploadId = UploadIdentifier.Create();

        Assert.StartsWith("upload-", uploadId, StringComparison.Ordinal);
        Assert.Equal(UploadIdentifier.Prefix.Length + UploadIdentifier.RandomHexLength, uploadId.Length);
        Assert.Matches("^upload-[0-9a-f]{32}$", uploadId);
        Assert.True(UploadIdentifier.IsWellFormed(uploadId));
    }

    [Fact]
    public void TwoIdentifiersDiffer()
    {
        Assert.NotEqual(UploadIdentifier.Create(), UploadIdentifier.Create());
    }

    [Fact]
    public void AnIdentifierIsOneValidSegmentOfAStorageKey()
    {
        var key = ObjectKey.FromSegments("staging", UploadIdentifier.Create(), "content");

        Assert.Equal(3, key.Segments.Count);
        Assert.Equal("staging", key.Segments[0]);
        Assert.Equal("content", key.Segments[2]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("upload-")]
    [InlineData("upload-0123456789abcdef0123456789abcde")]
    [InlineData("upload-0123456789abcdef0123456789abcdef0")]
    [InlineData("upload-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("upload-0123456789abcdef0123456789abcdeg")]
    [InlineData("Upload-0123456789abcdef0123456789abcdef")]
    [InlineData("object-0123456789abcdef0123456789abcdef")]
    [InlineData("upload-0123456789abcdef0123456789abcde/")]
    public void AnythingElseIsNotAnIdentifier(string? value)
    {
        Assert.False(UploadIdentifier.IsWellFormed(value));
    }
}
