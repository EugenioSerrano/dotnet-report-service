using ReportService.Api.Http;

namespace ReportService.Api.Tests.Http;

/// <summary>
/// The malformed-request contract, one case per row: the reason, the known field
/// path, the constant detail and the extension members an answer carries. None of
/// the texts can hold a value the caller sent, and the count of unknown members is
/// the only thing an answer says about them.
/// </summary>
public sealed class RequestInvalidDetailsTests
{
    [Fact]
    public void TheMediaTypeConditionNamesNoField()
    {
        var problem = InvalidRequestBody.MediaType();

        Assert.Equal(RequestInvalidDetails.MediaTypeReason, problem.Reason);
        Assert.Null(problem.Field);
        Assert.Equal("The request body must be sent as application/json.", problem.Detail);
        Assert.False(problem.Extensions.ContainsKey("unknownFieldCount"));
    }

    [Fact]
    public void TheMalformedBodyConditionNamesNoField()
    {
        var problem = InvalidRequestBody.MalformedJson();

        Assert.Equal(RequestInvalidDetails.MalformedJsonReason, problem.Reason);
        Assert.Null(problem.Field);
        Assert.Equal("The request body must be a single JSON object.", problem.Detail);
    }

    [Theory]
    [InlineData(1, "The request contains 1 field that is not part of this operation.")]
    [InlineData(3, "The request contains 3 fields that are not part of this operation.")]
    public void TheUnknownMemberConditionCarriesTheCount(int count, string detail)
    {
        var problem = InvalidRequestBody.UnknownField(count);

        Assert.Equal(RequestInvalidDetails.UnknownFieldReason, problem.Reason);
        Assert.Null(problem.Field);
        Assert.Equal(count, problem.UnknownFieldCount);
        Assert.Equal(detail, problem.Detail);
        Assert.Equal(count, problem.Extensions["unknownFieldCount"]);
    }

    [Fact]
    public void TheDuplicateConditionNamesTheKnownPath()
    {
        var problem = InvalidRequestBody.DuplicateField("mode");

        Assert.Equal(RequestInvalidDetails.DuplicateFieldReason, problem.Reason);
        Assert.Equal("mode", problem.Field);
        Assert.Equal("The field 'mode' appears more than once.", problem.Detail);
    }

    [Fact]
    public void TheMissingConditionNamesTheKnownPath()
    {
        var problem = InvalidRequestBody.MissingField("profile");

        Assert.Equal(RequestInvalidDetails.MissingFieldReason, problem.Reason);
        Assert.Equal("profile", problem.Field);
        Assert.Equal("The field 'profile' is required.", problem.Detail);
    }

    [Fact]
    public void TheNullConditionNamesTheKnownPath()
    {
        var problem = InvalidRequestBody.NullField("mode");

        Assert.Equal(RequestInvalidDetails.NullFieldReason, problem.Reason);
        Assert.Equal("mode", problem.Field);
        Assert.Equal("The field 'mode' must not be null.", problem.Detail);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("array")]
    [InlineData("object")]
    public void TheTypeConditionStatesTheExpectedTypeAndNeverTheSentOne(string expected)
    {
        var problem = InvalidRequestBody.WrongType("mode", expected);

        Assert.Equal(RequestInvalidDetails.WrongTypeReason, problem.Reason);
        Assert.Equal("mode", problem.Field);
        Assert.Equal($"The field 'mode' must be a {expected}.", problem.Detail);
    }

    [Fact]
    public void TheFormatConditionUsesTheConstantSentenceItsReaderDeclared()
    {
        var problem = InvalidRequestBody.InvalidFormat("sha256", "The field 'sha256' must be 64 hexadecimal characters.");

        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.Reason);
        Assert.Equal("sha256", problem.Field);
        Assert.Equal("The field 'sha256' must be 64 hexadecimal characters.", problem.Detail);
    }

    [Fact]
    public void EveryConditionCarriesItsFieldAndReasonAsExtensionMembers()
    {
        var problem = InvalidRequestBody.MissingField("mode");

        Assert.Equal("mode", problem.Extensions["field"]);
        Assert.Equal(RequestInvalidDetails.MissingFieldReason, problem.Extensions["reason"]);
        Assert.Equal(RequestInvalidDetails.Title, "Invalid request");
    }
}
