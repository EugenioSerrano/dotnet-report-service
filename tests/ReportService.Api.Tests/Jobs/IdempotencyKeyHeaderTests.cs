using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ReportService.Api.Http;
using ReportService.Api.Jobs;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The optional <c>Idempotency-Key</c> header: exactly one field, one to 255 visible ASCII
/// characters, kept exactly as it was received. The boundary values are exercised at both
/// ends, a malformed header is answered as a malformed request naming the header and never
/// the value, and the header rule is checked against the pattern the database column carries
/// so the two cannot slide apart.
/// </summary>
public sealed class IdempotencyKeyHeaderTests
{
    // The column's guard, verbatim from the migration that adds the column.
    private static readonly Regex ColumnPattern = new("^[!-~]{1,255}$");

    [Fact]
    public void NoHeaderIsAbsent()
    {
        Assert.IsType<IdempotencyKeyHeader.Result.Absent>(IdempotencyKeyHeader.Read(Request()));
    }

    [Theory]
    [InlineData("!")]
    [InlineData("~")]
    [InlineData("a")]
    [InlineData("3f8a21-plated")]
    [InlineData("A1b2C3d4E5f6G7h8I9j0")]
    public void AKeyOfVisibleAsciiIsPresentAndKeptAsReceived(string key)
    {
        var present = Assert.IsType<IdempotencyKeyHeader.Result.Present>(
            IdempotencyKeyHeader.Read(Request(key)));

        Assert.Equal(key, present.Key);
    }

    [Fact]
    public void AKeyOfTheLongestAdmittedLengthIsPresent()
    {
        var key = new string('K', IdempotencyKeyHeader.MaxLength);

        var present = Assert.IsType<IdempotencyKeyHeader.Result.Present>(
            IdempotencyKeyHeader.Read(Request(key)));

        Assert.Equal(key, present.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has\ttab")]
    [InlineData("café")]
    [InlineData("line\nbreak")]
    public void AKeyOutsideTheRuleIsRefused(string key)
    {
        var invalid = Assert.IsType<IdempotencyKeyHeader.Result.Invalid>(
            IdempotencyKeyHeader.Read(Request(key)));

        Assert.Equal(IdempotencyKeyHeader.InvalidDetail, invalid.Problem.Detail);
        Assert.Equal(IdempotencyKeyHeader.HeaderName, invalid.Problem.Field);
        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, invalid.Problem.Reason);
        if (key.Length > 0)
        {
            Assert.DoesNotContain(key, invalid.Problem.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AKeyAboveTheLongestAdmittedLengthIsRefused()
    {
        var tooLong = new string('K', IdempotencyKeyHeader.MaxLength + 1);

        Assert.IsType<IdempotencyKeyHeader.Result.Invalid>(
            IdempotencyKeyHeader.Read(Request(tooLong)));
    }

    [Fact]
    public void AHeaderSentTwiceIsRefused()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[IdempotencyKeyHeader.HeaderName] = new StringValues(["K1", "K1"]);

        Assert.IsType<IdempotencyKeyHeader.Result.Invalid>(IdempotencyKeyHeader.Read(request));
    }

    [Theory]
    [InlineData("!")]
    [InlineData("~")]
    [InlineData("K1")]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has\ttab")]
    [InlineData("café")]
    public void TheHeaderRuleAgreesWithTheColumnGuard(string key)
    {
        // One value at each boundary, checked by both rules: the API's and the column's. A value
        // one admits and the other refuses would let a request reach the store only to fail there.
        Assert.Equal(ColumnPattern.IsMatch(key), IdempotencyKeyHeader.IsKey(key));
    }

    [Fact]
    public void TheLongestAdmittedKeyIsAtTheColumnGuardsBoundary()
    {
        var longest = new string('K', IdempotencyKeyHeader.MaxLength);
        var tooLong = longest + "K";

        Assert.True(IdempotencyKeyHeader.IsKey(longest));
        Assert.Matches(ColumnPattern, longest);
        Assert.False(IdempotencyKeyHeader.IsKey(tooLong));
        Assert.DoesNotMatch(ColumnPattern, tooLong);
    }

    private static HttpRequest Request(string? key = null)
    {
        var request = new DefaultHttpContext().Request;
        if (key is not null)
        {
            request.Headers[IdempotencyKeyHeader.HeaderName] = key;
        }

        return request;
    }
}
