using System.Text;
using Microsoft.AspNetCore.Http;
using ReportService.Api.Http;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The declared consumer identity: the header must arrive exactly once, carry one
/// value and name something the registry's grammar admits. Everything else — the
/// header absent, empty, repeated, joined by commas, or holding a character or a
/// length the registry does not admit — leaves the request without an identity,
/// and the value is read as sent, never trimmed or folded. The identity header
/// alone is read byte for byte, so a caller that is not UTF-8 still reaches the
/// grammar; every other header keeps the framework's own reading.
/// </summary>
public sealed class ConsumerIdentityTests
{
    [Theory]
    [InlineData("X-Consumer-Id")]
    [InlineData("x-consumer-id")]
    [InlineData("X-CONSUMER-ID")]
    public void TheIdentityHeaderIsReadByteForByte(string headerName)
    {
        Assert.Equal(Encoding.Latin1, ConsumerIdentity.HeaderDecoding(headerName));
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("X-Consumer-Id-Suffix")]
    [InlineData("Accept")]
    public void EveryOtherHeaderKeepsTheFrameworksOwnReading(string headerName)
    {
        Assert.Null(ConsumerIdentity.HeaderDecoding(headerName));
    }

    [Fact]
    public void AReadingIsOnlyAskedWithAHeaderName()
    {
        Assert.Throws<ArgumentNullException>(() => ConsumerIdentity.HeaderDecoding(null!));
    }

    [Fact]
    public void AHeaderWithOneValidValueIsTheIdentity()
    {
        Assert.True(ConsumerIdentity.TryRead(Request("dev-consumer"), out var consumerId));
        Assert.Equal("dev-consumer", consumerId);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("A.b_c-9")]
    [InlineData("dev.consumer_1-2")]
    public void EveryCharacterOfTheGrammarAndOneCharacterAreAccepted(string value)
    {
        Assert.True(ConsumerIdentity.TryRead(Request(value), out var consumerId));
        Assert.Equal(value, consumerId);
    }

    [Fact]
    public void OneHundredAndTwentyEightCharactersAreAccepted()
    {
        var value = new string('a', ConsumerIdentity.MaxLength);

        Assert.True(ConsumerIdentity.TryRead(Request(value), out var consumerId));
        Assert.Equal(value, consumerId);
    }

    [Fact]
    public void OneHundredAndTwentyNineCharactersAreRefused()
    {
        Assert.False(ConsumerIdentity.TryRead(Request(new string('a', ConsumerIdentity.MaxLength + 1)), out var consumerId));
        Assert.Equal(string.Empty, consumerId);
    }

    [Fact]
    public void AnAbsentHeaderLeavesNoIdentity()
    {
        Assert.False(ConsumerIdentity.TryRead(new DefaultHttpContext().Request, out var consumerId));
        Assert.Equal(string.Empty, consumerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dev consumer")]
    [InlineData("dev/consumer")]
    [InlineData("cónsumer")]
    [InlineData("consumer;drop")]
    public void AnUnusableValueLeavesNoIdentity(string value)
    {
        Assert.False(ConsumerIdentity.TryRead(Request(value), out _));
    }

    [Fact]
    public void ARepeatedHeaderLeavesNoIdentity()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[ConsumerIdentity.HeaderName] = new Microsoft.Extensions.Primitives.StringValues(
            new[] { "first-consumer", "second-consumer" });

        Assert.False(ConsumerIdentity.TryRead(request, out _));
    }

    [Fact]
    public void AValueJoinedByACommaLeavesNoIdentity()
    {
        Assert.False(ConsumerIdentity.TryRead(Request("first-consumer,second-consumer"), out _));
    }

    [Fact]
    public void TheValueIsNotFolded()
    {
        Assert.True(ConsumerIdentity.TryRead(Request("Dev-Consumer"), out var consumerId));
        Assert.Equal("Dev-Consumer", consumerId);
    }

    private static HttpRequest Request(string value)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[ConsumerIdentity.HeaderName] = value;
        return request;
    }
}
