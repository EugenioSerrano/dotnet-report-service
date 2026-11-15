using System.Text;
using Microsoft.AspNetCore.Http;
using ReportService.Api.Http;
using ReportService.Api.Jobs;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// Reading the document that creates a job: one JSON object, only the known members
/// at every depth, each once and each of its declared type, at least one output named
/// once, the two syntax rules of the template reference, and the presentation — a
/// culture and a time zone, each optional, each a bounded printable word. The cases
/// below fix what each refusal says and which one wins when the document has several
/// faults, and they check that no value, length or type name of the caller reaches an
/// answer.
/// </summary>
public sealed class CreateReportJobRequestReaderTests
{
    private const string Upload = "upload-0123456789abcdef0123456789abcdef";
    private const string Planted = "planted-secret-3f8a21";

    private static string Valid(string? version = "1.0.0") => version is null
        ? $$"""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"{{Upload}}","profile":"header-items-v1"},"outputs":["pdf"]}"""
        : $$"""{"template":{"id":"pilot-grouped","version":"{{version}}"},"data":{"uploadId":"{{Upload}}","profile":"header-items-v1"},"outputs":["pdf"]}""";

    // A valid document whose presentation is the JSON value written here, exactly as sent.
    private static string WithRender(string render) =>
        $$"""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"{{Upload}}","profile":"header-items-v1"},"outputs":["pdf"],"render":{{render}}}""";

    [Fact]
    public async Task AValidDocumentIsAcceptedWithEveryMemberAsSent()
    {
        var result = await ReadAsync(Valid());

        var accepted = Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(result);
        Assert.Equal("pilot-grouped", accepted.Request.TemplateId);
        Assert.Equal("1.0.0", accepted.Request.TemplateVersion);
        Assert.Equal(Upload, accepted.Request.UploadId);
        Assert.Equal("header-items-v1", accepted.Request.Profile);
        Assert.Equal(["pdf"], accepted.Request.Outputs);
    }

    [Fact]
    public async Task AReferenceWithoutAVersionCarriesNone()
    {
        var accepted = Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(await ReadAsync(Valid(version: null)));

        Assert.Null(accepted.Request.TemplateVersion);
    }

    [Fact]
    public async Task APresentationOfBothValuesIsAcceptedAsSent()
    {
        var accepted = Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(
            await ReadAsync(WithRender("""{"culture":"es-AR","timeZone":"America/Argentina/Buenos_Aires"}""")));

        Assert.Equal("es-AR", accepted.Request.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", accepted.Request.TimeZone);
    }

    [Fact]
    public async Task AnEmptyPresentationObjectNamesNoValue()
    {
        var accepted = Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(await ReadAsync(WithRender("{}")));

        Assert.Null(accepted.Request.Culture);
        Assert.Null(accepted.Request.TimeZone);
    }

    [Theory]
    [InlineData("""{"culture":"es-AR"}""", "es-AR", null)]
    [InlineData("""{"timeZone":"Europe/Madrid"}""", null, "Europe/Madrid")]
    public async Task EitherPresentationValueAloneIsAccepted(string render, string? culture, string? timeZone)
    {
        var accepted = Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(await ReadAsync(WithRender(render)));

        Assert.Equal(culture, accepted.Request.Culture);
        Assert.Equal(timeZone, accepted.Request.TimeZone);
    }

    [Fact]
    public async Task APresentationValueOfTheLongestLengthIsAccepted()
    {
        var culture = new string('c', 64);
        var timeZone = new string('z', 64);

        var accepted = Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(
            await ReadAsync(WithRender($$"""{"culture":"{{culture}}","timeZone":"{{timeZone}}"}""")));

        Assert.Equal(culture, accepted.Request.Culture);
        Assert.Equal(timeZone, accepted.Request.TimeZone);
    }

    [Theory]
    [InlineData("""{"culture":""}""", "render.culture", CreateReportJobRequestReader.RenderCultureFormatDetail)]
    [InlineData("""{"timeZone":""}""", "render.timeZone", CreateReportJobRequestReader.RenderTimeZoneFormatDetail)]
    [InlineData("""{"culture":"es AR"}""", "render.culture", CreateReportJobRequestReader.RenderCultureFormatDetail)]
    [InlineData("""{"culture":"es-ARñ"}""", "render.culture", CreateReportJobRequestReader.RenderCultureFormatDetail)]
    [InlineData("""{"timeZone":"Europe/Madrid "}""", "render.timeZone", CreateReportJobRequestReader.RenderTimeZoneFormatDetail)]
    public async Task APresentationValueOutsideTheWireRuleIsRefusedWithItsConstant(
        string render,
        string field,
        string detail)
    {
        var problem = await RejectedAsync(WithRender(render));

        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.Reason);
        Assert.Equal(field, problem.Field);
        Assert.Equal(detail, problem.Detail);
    }

    [Fact]
    public async Task APresentationValueOutsideTheWireRuleIsNotEchoed()
    {
        var problem = await RejectedAsync(WithRender($$"""{"culture":"{{Planted}} x"}"""));

        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.Reason);
        Assert.Equal("render.culture", problem.Field);
        Assert.DoesNotContain(Planted, problem.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("culture")]
    [InlineData("timeZone")]
    public async Task APresentationValueOneCharacterOverTheBoundIsRefused(string member)
    {
        var overlong = new string('x', 65);

        var problem = await RejectedAsync(WithRender($$"""{"{{member}}":"{{overlong}}"}"""));

        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.Reason);
        Assert.Equal($"render.{member}", problem.Field);
        Assert.DoesNotContain(overlong, problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWellFormedRequestForSomethingUnsupportedIsAcceptedHere()
    {
        // Support is decided after the document is read, so the two refusals stay apart.
        var body = $$"""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"{{Upload}}","profile":"header-items-v2"},"outputs":["html","pdf"]}""";

        Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(await ReadAsync(body));
    }

    [Fact]
    public async Task ExtraWhitespaceIsAccepted()
    {
        var body = " \n " + Valid() + " \t\n ";

        Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(await ReadAsync(body));
    }

    [Fact]
    public async Task AByteOrderMarkIsSkipped()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Valid())).ToArray();

        Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(await ReadAsync(body));
    }

    [Fact]
    public async Task ADocumentOfExactlyTheBoundIsRead()
    {
        var body = Valid().PadRight(CreateReportJobRequestReader.MaxBodyBytes);

        Assert.Equal(CreateReportJobRequestReader.MaxBodyBytes, body.Length);
        Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(await ReadAsync(body));
    }

    [Fact]
    public async Task ADocumentOneByteOverTheBoundIsTooLarge()
    {
        var body = Valid().PadRight(CreateReportJobRequestReader.MaxBodyBytes + 1);

        Assert.IsType<CreateReportJobRequestReader.Result.TooLarge>(await ReadAsync(body));
    }

    [Fact]
    public async Task AnUndeclaredBodyLengthIsBoundedWhileItIsRead()
    {
        var body = Valid().PadRight(CreateReportJobRequestReader.MaxBodyBytes + 400);

        Assert.IsType<CreateReportJobRequestReader.Result.TooLarge>(
            await CreateReportJobRequestReader.ReadAsync(Request(body, length: null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABodyOfAnotherMediaTypeIsReportedByItsOwnReason()
    {
        var problem = await RejectedAsync(Valid(), contentType: "text/plain");

        Assert.Equal(RequestInvalidDetails.MediaTypeReason, problem.Reason);
        Assert.Null(problem.Field);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"a string\"")]
    [InlineData("{} {}")]
    [InlineData("{\"template\":{},\"data\":{},\"outputs\":[\"pdf\"]} trailing")]
    [InlineData("{\"template\":{\"id\":\"pilot-grouped\"}")]
    public async Task ADocumentThatIsNotOneObjectIsReportedAsMalformed(string body)
    {
        var problem = await RejectedAsync(body);

        Assert.Equal(RequestInvalidDetails.MalformedJsonReason, problem.Reason);
    }

    [Theory]
    [InlineData("""{"metadata":{},"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", 1)]
    [InlineData("""{"metadata":{},"externalReference":{},"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", 2)]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"render":{"paper":"a4"}}""", 1)]
    [InlineData("""{"template":{"id":"pilot-grouped","extra":"planted-secret-3f8a21"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", 1)]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1","more":{}},"outputs":["pdf"]}""", 1)]
    public async Task AnUnknownMemberIsCountedAndNeverNamed(string body, int count)
    {
        var problem = await RejectedAsync(body);

        Assert.Equal(RequestInvalidDetails.UnknownFieldReason, problem.Reason);
        Assert.Null(problem.Field);
        Assert.Equal(count, problem.UnknownFieldCount);
        Assert.Equal(count, problem.Extensions["unknownFieldCount"]);
        Assert.DoesNotContain(Planted, problem.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"template":{"id":"pilot-grouped"},"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"data":{},"outputs":["pdf"]}""", "data")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"outputs":["pdf"]}""", "outputs")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"render":{},"render":{}}""", "render")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"render":{"culture":"es-AR","culture":"en-GB"}}""", "render.culture")]
    [InlineData("""{"template":{"id":"pilot-grouped","id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template.id")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "data.uploadId")]
    public async Task AMemberWrittenTwiceIsReportedByItsPath(string body, string field)
    {
        var problem = await RejectedAsync(body);

        Assert.Equal(RequestInvalidDetails.DuplicateFieldReason, problem.Reason);
        Assert.Equal(field, problem.Field);
    }

    [Theory]
    [InlineData("""{"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template")]
    [InlineData("""{"template":{"version":"1.0.0"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template.id")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"outputs":["pdf"]}""", "data")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"profile":"header-items-v1"},"outputs":["pdf"]}""", "data.uploadId")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef"},"outputs":["pdf"]}""", "data.profile")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"}}""", "outputs")]
    public async Task AMissingMemberIsReportedByItsPath(string body, string field)
    {
        var problem = await RejectedAsync(body);

        Assert.Equal(RequestInvalidDetails.MissingFieldReason, problem.Reason);
        Assert.Equal(field, problem.Field);
        Assert.Equal($"The field '{field}' is required.", problem.Detail);
    }

    [Theory]
    [InlineData("""{"template":null,"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template")]
    [InlineData("""{"template":{"id":null},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template.id")]
    [InlineData("""{"template":{"id":"pilot-grouped","version":null},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template.version")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":null,"outputs":["pdf"]}""", "data")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":null,"profile":"header-items-v1"},"outputs":["pdf"]}""", "data.uploadId")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":null},"outputs":["pdf"]}""", "data.profile")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":null}""", "outputs")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"render":null}""", "render")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"render":{"culture":null}}""", "render.culture")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"render":{"timeZone":null}}""", "render.timeZone")]
    public async Task ANullMemberIsReportedByItsPath(string body, string field)
    {
        var problem = await RejectedAsync(body);

        Assert.Equal(RequestInvalidDetails.NullFieldReason, problem.Reason);
        Assert.Equal(field, problem.Field);
    }

    [Theory]
    [InlineData("""{"template":[],"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template", "object")]
    [InlineData("""{"template":"pilot-grouped","data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template", "object")]
    [InlineData("""{"template":{"id":17},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template.id", "string")]
    [InlineData("""{"template":{"id":"pilot-grouped","version":1},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}""", "template.version", "string")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":"upload-0123456789abcdef0123456789abcdef","outputs":["pdf"]}""", "data", "object")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":["upload-0123456789abcdef0123456789abcdef"],"profile":"header-items-v1"},"outputs":["pdf"]}""", "data.uploadId", "string")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":true},"outputs":["pdf"]}""", "data.profile", "string")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":{"a":"pdf"}}""", "outputs", "array")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"render":"es-AR"}""", "render", "object")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"render":[]}""", "render", "object")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"render":{"culture":42}}""", "render.culture", "string")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"],"render":{"timeZone":true}}""", "render.timeZone", "string")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf",7]}""", "outputs", "array of strings")]
    [InlineData("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf",{"planted":"planted-secret-3f8a21"}]}""", "outputs", "array of strings")]
    public async Task AMemberOfAnotherTypeIsReportedWithTheExpectedTypeOnly(string body, string field, string expected)
    {
        var problem = await RejectedAsync(body);

        Assert.Equal(RequestInvalidDetails.WrongTypeReason, problem.Reason);
        Assert.Equal(field, problem.Field);
        Assert.Equal($"The field '{field}' must be a {expected}.", problem.Detail);
        Assert.DoesNotContain(Planted, problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyOutputsArrayIsRefusedWithItsOwnConstant()
    {
        var problem = await RejectedAsync("""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":[]}""");

        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.Reason);
        Assert.Equal("outputs", problem.Field);
        Assert.Equal(CreateReportJobRequestReader.EmptyOutputsDetail, problem.Detail);
    }

    [Fact]
    public async Task ARepeatedOutputIsRefusedAndTheValueIsNotEchoed()
    {
        var problem = await RejectedAsync($$"""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["{{Planted}}","{{Planted}}"]}""");

        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.Reason);
        Assert.Equal("outputs", problem.Field);
        Assert.Equal(CreateReportJobRequestReader.RepeatedOutputDetail, problem.Detail);
        Assert.DoesNotContain(Planted, problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATemplateIdentifierOfTheLongestLengthIsAccepted()
    {
        var identifier = "a" + new string('b', 126) + "9";

        var accepted = Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(
            await ReadAsync($$"""{"template":{"id":"{{identifier}}"},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef","profile":"header-items-v1"},"outputs":["pdf"]}"""));

        Assert.Equal(128, accepted.Request.TemplateId.Length);
    }

    [Fact]
    public async Task ATemplateVersionOfTheLongestLengthIsAccepted()
    {
        var version = new string('v', 128);

        var accepted = Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(await ReadAsync(Valid(version)));

        Assert.Equal(version, accepted.Request.TemplateVersion);
    }

    [Fact]
    public async Task AMemberMissingWinsOverTheValueOfAnother()
    {
        // Precedence: a missing member is reported before a wrong type, so the same fault
        // set always produces the same answer.
        var problem = await RejectedAsync("""{"template":{"id":17},"data":{"uploadId":"upload-0123456789abcdef0123456789abcdef"}}""");

        Assert.Equal(RequestInvalidDetails.MissingFieldReason, problem.Reason);
        Assert.Equal("data.profile", problem.Field);
    }

    private static async Task<CreateReportJobRequestReader.Result> ReadAsync(
        string body,
        string contentType = CreateReportJobRequestReader.JsonMediaType) =>
        await CreateReportJobRequestReader.ReadAsync(
            Request(body, ContentLength(body), contentType),
            TestContext.Current.CancellationToken);

    private static async Task<CreateReportJobRequestReader.Result> ReadAsync(byte[] body) =>
        await CreateReportJobRequestReader.ReadAsync(
            Request(Encoding.UTF8.GetString(body), body.Length),
            TestContext.Current.CancellationToken);

    private static async Task<InvalidRequestBody> RejectedAsync(
        string body,
        string contentType = CreateReportJobRequestReader.JsonMediaType)
    {
        var result = await ReadAsync(body, contentType);
        return Assert.IsType<CreateReportJobRequestReader.Result.Rejected>(result).Problem;
    }

    // A null length is a body sent without one; the reader must bound it while it reads.
    private static HttpRequest Request(
        string body,
        long? length,
        string contentType = CreateReportJobRequestReader.JsonMediaType)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        context.Request.ContentLength = length;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context.Request;
    }

    private static long ContentLength(string body) => Encoding.UTF8.GetByteCount(body);
}
