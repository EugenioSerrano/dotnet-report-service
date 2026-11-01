using System.Text;
using Microsoft.AspNetCore.Http;
using ReportService.Api.Http;
using ReportService.Api.Uploads;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// Reading the control document that opens a session: one JSON object, exactly the
/// two known members, each once and each a string, within the size bound. The
/// cases below fix what each refusal says and which one wins when the document has
/// several faults, and they check that no value, length or type name of the caller
/// reaches an answer.
/// </summary>
public sealed class CreateUploadRequestReaderTests
{
    private const string ValidJson = """{"profile":"header-items-v1","mode":"API_STREAM"}""";

    [Fact]
    public async Task AValidDocumentIsAcceptedWithBothValuesAsSent()
    {
        var result = await ReadAsync(ValidJson);

        var accepted = Assert.IsType<CreateUploadRequestReader.Result.Accepted>(result);
        Assert.Equal("header-items-v1", accepted.Request.Profile);
        Assert.Equal("API_STREAM", accepted.Request.Mode);
    }

    [Fact]
    public async Task ExtraWhitespaceIsAccepted()
    {
        var result = await ReadAsync(" \n { \"mode\" : \"API_STREAM\" , \"profile\" : \"header-items-v1\" } \t\n ");

        Assert.IsType<CreateUploadRequestReader.Result.Accepted>(result);
    }

    [Fact]
    public async Task AByteOrderMarkIsSkipped()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(ValidJson)).ToArray();

        Assert.IsType<CreateUploadRequestReader.Result.Accepted>(await ReadAsync(body));
    }

    [Fact]
    public async Task AnUnsupportedButWellFormedValueIsAcceptedHere()
    {
        // Support is decided after the document is read, so a well-formed request stays distinguishable.
        var result = await ReadAsync("""{"profile":"header-items-v2","mode":"DIRECT_STORAGE"}""");

        Assert.IsType<CreateUploadRequestReader.Result.Accepted>(result);
    }

    [Fact]
    public async Task AMissingModeIsReportedAsARequiredField()
    {
        var problem = await RejectedAsync("""{"profile":"header-items-v1"}""");

        Assert.Equal(RequestInvalidDetails.MissingFieldReason, problem.Reason);
        Assert.Equal("mode", problem.Field);
        Assert.Equal("The field 'mode' is required.", problem.Detail);
    }

    [Fact]
    public async Task AMissingProfileIsReportedAsARequiredField()
    {
        var problem = await RejectedAsync("""{"mode":"API_STREAM"}""");

        Assert.Equal(RequestInvalidDetails.MissingFieldReason, problem.Reason);
        Assert.Equal("profile", problem.Field);
        Assert.Equal("The field 'profile' is required.", problem.Detail);
    }

    [Fact]
    public async Task ANullValueIsReportedAsNull()
    {
        var problem = await RejectedAsync("""{"profile":"header-items-v1","mode":null}""");

        Assert.Equal(RequestInvalidDetails.NullFieldReason, problem.Reason);
        Assert.Equal("mode", problem.Field);
        Assert.Equal("The field 'mode' must not be null.", problem.Detail);
    }

    [Theory]
    [InlineData("""{"profile":"header-items-v1","mode":12345678901}""")]
    [InlineData("""{"profile":"header-items-v1","mode":{"planted":"x"}}""")]
    [InlineData("""{"profile":"header-items-v1","mode":["API_STREAM"]}""")]
    [InlineData("""{"profile":"header-items-v1","mode":true}""")]
    public async Task AValueOfAnotherTypeIsReportedWithTheExpectedTypeOnly(string body)
    {
        var problem = await RejectedAsync(body);

        Assert.Equal(RequestInvalidDetails.WrongTypeReason, problem.Reason);
        Assert.Equal("mode", problem.Field);
        Assert.Equal("The field 'mode' must be a string.", problem.Detail);
        Assert.DoesNotContain("12345678901", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("planted", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("number", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedMemberIsReportedAsADuplicate()
    {
        var problem = await RejectedAsync("""{"profile":"header-items-v1","mode":"API_STREAM","mode":"API_STREAM"}""");

        Assert.Equal(RequestInvalidDetails.DuplicateFieldReason, problem.Reason);
        Assert.Equal("mode", problem.Field);
        Assert.Equal("The field 'mode' appears more than once.", problem.Detail);
    }

    [Fact]
    public async Task OneUnknownMemberIsCounted()
    {
        var problem = await RejectedAsync("""{"profile":"header-items-v1","mode":"API_STREAM","sha256":"x"}""");

        Assert.Equal(RequestInvalidDetails.UnknownFieldReason, problem.Reason);
        Assert.Null(problem.Field);
        Assert.Equal(1, problem.UnknownFieldCount);
        Assert.Equal("The request contains 1 field that is not part of this operation.", problem.Detail);
    }

    [Fact]
    public async Task SeveralUnknownMembersAreCountedAndNeverNamed()
    {
        var problem = await RejectedAsync("""{"profile":"header-items-v1","mode":"API_STREAM","sha256":"x","<script>":"y","zé":"z"}""");

        Assert.Equal(RequestInvalidDetails.UnknownFieldReason, problem.Reason);
        Assert.Equal(3, problem.UnknownFieldCount);
        Assert.Equal("The request contains 3 fields that are not part of this operation.", problem.Detail);
        Assert.DoesNotContain("sha256", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemberNameDifferingOnlyInCaseIsAnUnknownMember()
    {
        var problem = await RejectedAsync("""{"Profile":"header-items-v1","mode":"API_STREAM"}""");

        Assert.Equal(RequestInvalidDetails.UnknownFieldReason, problem.Reason);
        Assert.Equal(1, problem.UnknownFieldCount);
    }

    [Fact]
    public async Task ARepeatedUnknownMemberCountsTwice()
    {
        var problem = await RejectedAsync("""{"profile":"header-items-v1","mode":"API_STREAM","sha256":"x","sha256":"y"}""");

        Assert.Equal(RequestInvalidDetails.UnknownFieldReason, problem.Reason);
        Assert.Equal(2, problem.UnknownFieldCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData(""""a string"""")]
    [InlineData("""{"profile":"header-items-v1","mode":"API_STREAM"} {}""")]
    [InlineData("""{"profile":"header-items-v1","mode":"API_STREAM"},""")]
    [InlineData("""{"profile":"header-items-v1","mode":"API_STREAM" """)]
    [InlineData("""{"profile":"header-items-v1","mode":}""")]
    [InlineData("""{"profile":"header-items-v1","mode":"API_STREAM","trailing":1,}""")]
    public async Task ABodyThatIsNotOneJsonObjectIsReportedAsMalformed(string body)
    {
        var problem = await RejectedAsync(body);

        Assert.Equal(RequestInvalidDetails.MalformedJsonReason, problem.Reason);
        Assert.Null(problem.Field);
        Assert.Equal("The request body must be a single JSON object.", problem.Detail);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/json-seq")]
    [InlineData(null)]
    public async Task AnotherMediaTypeIsRefusedBeforeTheBodyIsRead(string? contentType)
    {
        var request = Request(Encoding.UTF8.GetBytes(ValidJson), contentType, ValidJson.Length);
        request.Body = new UnreadableStream();

        var problem = await RejectedAsync(request);

        Assert.Equal(RequestInvalidDetails.MediaTypeReason, problem.Reason);
        Assert.Null(problem.Field);
        Assert.Equal("The request body must be sent as application/json.", problem.Detail);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON")]
    public async Task JsonWithParametersIsAccepted(string contentType)
    {
        var request = Request(Encoding.UTF8.GetBytes(ValidJson), contentType, ValidJson.Length);

        Assert.IsType<CreateUploadRequestReader.Result.Accepted>(await CreateUploadRequestReader.ReadAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ADocumentOfExactlyTheBoundIsAccepted()
    {
        var body = Encoding.UTF8.GetBytes(ValidJson + new string(' ', CreateUploadRequestReader.MaxBodyBytes - ValidJson.Length));

        Assert.Equal(CreateUploadRequestReader.MaxBodyBytes, body.Length);
        Assert.IsType<CreateUploadRequestReader.Result.Accepted>(await ReadAsync(body));
    }

    [Fact]
    public async Task ADocumentOneByteOverTheBoundIsRefusedAsTooLarge()
    {
        var body = Encoding.UTF8.GetBytes(ValidJson + new string(' ', CreateUploadRequestReader.MaxBodyBytes + 1 - ValidJson.Length));

        Assert.Equal(CreateUploadRequestReader.MaxBodyBytes + 1, body.Length);
        Assert.IsType<CreateUploadRequestReader.Result.TooLarge>(await ReadAsync(body));
    }

    [Fact]
    public async Task ADeclaredLengthOverTheBoundIsRefusedWithoutReadingTheBody()
    {
        var request = Request([], "application/json", CreateUploadRequestReader.MaxBodyBytes + 1);
        request.Body = new UnreadableStream();

        Assert.IsType<CreateUploadRequestReader.Result.TooLarge>(await CreateUploadRequestReader.ReadAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABodyWithoutADeclaredLengthIsCountedUpToTheBound()
    {
        var body = Encoding.UTF8.GetBytes(ValidJson + new string(' ', CreateUploadRequestReader.MaxBodyBytes - ValidJson.Length + 1));
        var request = Request(body, "application/json", null);

        Assert.IsType<CreateUploadRequestReader.Result.TooLarge>(await CreateUploadRequestReader.ReadAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnknownMemberAndAMissingModeReportTheUnknownMember()
    {
        var problem = await RejectedAsync("""{"profile":"header-items-v1","sha256":"x"}""");

        Assert.Equal(RequestInvalidDetails.UnknownFieldReason, problem.Reason);
        Assert.Equal(1, problem.UnknownFieldCount);
    }

    [Fact]
    public async Task ARepeatedModeAndANullProfileReportTheDuplicate()
    {
        var problem = await RejectedAsync("""{"profile":null,"mode":"API_STREAM","mode":"API_STREAM"}""");

        Assert.Equal(RequestInvalidDetails.DuplicateFieldReason, problem.Reason);
        Assert.Equal("mode", problem.Field);
    }

    [Fact]
    public async Task TheFirstKnownMemberInTheReadersOrderDecides()
    {
        var problem = await RejectedAsync("""{"profile":7}""");

        Assert.Equal(RequestInvalidDetails.WrongTypeReason, problem.Reason);
        Assert.Equal("profile", problem.Field);
    }

    private static Task<CreateUploadRequestReader.Result> ReadAsync(string body) =>
        ReadAsync(Encoding.UTF8.GetBytes(body));

    private static Task<CreateUploadRequestReader.Result> ReadAsync(byte[] body) =>
        CreateUploadRequestReader.ReadAsync(
            Request(body, CreateUploadRequestReader.JsonMediaType, body.Length),
            TestContext.Current.CancellationToken);

    private static async Task<InvalidRequestBody> RejectedAsync(string body) =>
        await RejectedAsync(Request(Encoding.UTF8.GetBytes(body), CreateUploadRequestReader.JsonMediaType, Encoding.UTF8.GetByteCount(body)));

    private static async Task<InvalidRequestBody> RejectedAsync(HttpRequest request) =>
        Assert.IsType<CreateUploadRequestReader.Result.Rejected>(
            await CreateUploadRequestReader.ReadAsync(request, TestContext.Current.CancellationToken)).Problem;

    private static HttpRequest Request(byte[] body, string? contentType, long? contentLength)
    {
        var request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream(body);
        request.ContentType = contentType;
        request.ContentLength = contentLength;
        return request;
    }

    /// <summary>A body that fails the test the moment anything reads it.</summary>
    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("the body must not be read");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the body must not be read");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
