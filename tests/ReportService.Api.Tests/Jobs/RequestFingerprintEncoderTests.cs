using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// The identity a job request is fingerprinted by: the canonical form of what the consumer
/// asked for and nothing that depends on state — no version resolved later, no baseline, no
/// clock, no store. The cases fix the three published vectors, prove that every declared
/// member is encoded and that changing any one of them changes the identity, and prove that
/// two documents that ask for the same thing are the same request however they spell it,
/// while two different things never collide.
/// </summary>
public sealed class RequestFingerprintEncoderTests
{
    private const string Upload = "upload-3f2b0c9e8d7a4b6c9e1f0a2b3c4d5e6f";
    private const string Profile = "header-items-v1";
    private const string Template = "pilot-grouped";

    [Theory]
    [InlineData("consumer-a", "1.0.0", "v1:8f46096b8affc201b09a455505de5d71dade1f79058acf464394ac2876f8db19")]
    [InlineData("consumer-a", null, "v1:7d50aefb703fb91c36e5700ac2f72837cb5b3c732323a73ea938c8febe0d3b33")]
    [InlineData("consumer-b", "1.0.0", "v1:13924a976ba3611a162c683ad92665e091ce4845fdcdde6c0210c5fa7df2a6e0")]
    public void ThePublishedVectorsAreComputedExactly(string consumerId, string? version, string expected)
    {
        var fingerprint = RequestFingerprintEncoder.Compute(consumerId, Request(version: version));

        Assert.Equal(expected, fingerprint.Value);
    }

    [Fact]
    public void ChangingAnyOneDeclaredMemberChangesTheIdentity()
    {
        var original = Request();
        var reference = RequestFingerprintEncoder.Compute("consumer-a", original);
        var mutations = new Dictionary<string, (CreateReportJobRequest Request, string Consumer)>
        {
            [RequestFingerprintEncoder.ConsumerMember] = (original, "consumer-b"),
            [RequestFingerprintEncoder.UploadIdMember] = (original with { UploadId = "upload-00000000000000000000000000000001" }, "consumer-a"),
            [RequestFingerprintEncoder.ProfileMember] = (original with { Profile = "another-profile" }, "consumer-a"),
            [RequestFingerprintEncoder.TemplateIdMember] = (original with { TemplateId = "pilot-simple" }, "consumer-a"),
            [RequestFingerprintEncoder.TemplateVersionMember] = (original with { TemplateVersion = "1.1.0" }, "consumer-a"),
            [RequestFingerprintEncoder.CultureMember] = (original with { Culture = "es-AR" }, "consumer-a"),
            [RequestFingerprintEncoder.TimeZoneMember] = (original with { TimeZone = "UTC" }, "consumer-a"),
            [RequestFingerprintEncoder.OutputsMember] = (original with { Outputs = ["html"] }, "consumer-a"),
            [RequestFingerprintEncoder.PriorityMember] = (original with { Priority = "high" }, "consumer-a"),
        };

        // Every declared member has a mutation here: a member added to the table without one
        // fails this assertion before it can silently stop being exercised.
        Assert.Equal(
            RequestFingerprintEncoder.Members.Select(member => member.Name).Order(StringComparer.Ordinal),
            mutations.Keys.Order(StringComparer.Ordinal));

        foreach (var (member, mutation) in mutations)
        {
            Assert.NotEqual(
                reference.Value,
                RequestFingerprintEncoder.Compute(mutation.Consumer, mutation.Request).Value);
        }
    }

    [Theory]
    [InlineData("consumer-a", "1.0.0", "v1:8f46096b8affc201b09a455505de5d71dade1f79058acf464394ac2876f8db19")]
    [InlineData("consumer-a", null, "v1:7d50aefb703fb91c36e5700ac2f72837cb5b3c732323a73ea938c8febe0d3b33")]
    [InlineData("consumer-b", "1.0.0", "v1:13924a976ba3611a162c683ad92665e091ce4845fdcdde6c0210c5fa7df2a6e0")]
    public void ARequestDeclaringNormalKeepsThePublishedIdentityOfARequestDeclaringNone(string consumerId, string? version, string expected)
    {
        // A priority of normal is what an absent one means: the two are the same request, and
        // every identity computed before the priority existed is unchanged.
        var declared = Request(version: version) with { Priority = "normal" };

        Assert.Equal(expected, RequestFingerprintEncoder.Compute(consumerId, declared).Value);
    }

    [Fact]
    public void EachDeclaredPriorityOtherThanNormalIsItsOwnIdentity()
    {
        var none = RequestFingerprintEncoder.Compute("consumer-a", Request()).Value;
        var low = RequestFingerprintEncoder.Compute("consumer-a", Request() with { Priority = "low" }).Value;
        var high = RequestFingerprintEncoder.Compute("consumer-a", Request() with { Priority = "high" }).Value;

        Assert.Equal(3, new[] { none, low, high }.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null, JobPriority.Normal)]
    [InlineData("low", JobPriority.Low)]
    [InlineData("normal", JobPriority.Normal)]
    [InlineData("high", JobPriority.High)]
    public void TheDeclaredPriorityOfARequestIsTheLevelItNamesOrNormal(string? sent, JobPriority expected) =>
        Assert.Equal(expected, RequestFingerprintEncoder.DeclaredPriority(Request() with { Priority = sent }));

    [Fact]
    public void AnUnreadPriorityIsAnInvariantBreachNotAnInput() =>
        Assert.Throws<InvalidOperationException>(() =>
            RequestFingerprintEncoder.DeclaredPriority(Request() with { Priority = "urgent" }));

    [Fact]
    public void TheOutputsAreASetInOrdinalOrderWhateverOrderTheRequestNamedThem()
    {
        var one = Request(outputs: ["pdf", "html"]);
        var other = Request(outputs: ["html", "pdf"]);

        Assert.Equal(
            RequestFingerprintEncoder.Compute("consumer-a", one).Value,
            RequestFingerprintEncoder.Compute("consumer-a", other).Value);
    }

    [Fact]
    public void AVersionsBuildMetadataIsPartOfTheReferenceAsWritten()
    {
        var plain = Request(version: "1.0.0");
        var withBuild = Request(version: "1.0.0+build.5");

        Assert.NotEqual(
            RequestFingerprintEncoder.Compute("consumer-a", plain).Value,
            RequestFingerprintEncoder.Compute("consumer-a", withBuild).Value);
    }

    [Fact]
    public void AVersionNamedAndAVersionOmittedAreDifferentRequests()
    {
        Assert.NotEqual(
            RequestFingerprintEncoder.Compute("consumer-a", Request(version: "1.0.0")).Value,
            RequestFingerprintEncoder.Compute("consumer-a", Request(version: null)).Value);
    }

    [Fact]
    public async Task TwoDocumentsThatDifferOnlyInSpellingAreTheSameRequest()
    {
        var compact = await ReadAsync(
            $$"""{"template":{"id":"{{Template}}","version":"1.0.0"},"data":{"uploadId":"{{Upload}}","profile":"{{Profile}}"},"outputs":["pdf"]}""");
        var spaced = await ReadAsync(
            $$"""
            {
              "data": { "profile": "{{Profile}}", "uploadId": "{{Upload}}" },
              "outputs": [ "pdf" ],
              "template": { "version": "\u0031.0.0", "id": "{{Template}}" }
            }
            """);

        Assert.Equal(
            RequestFingerprintEncoder.Compute("consumer-a", compact).Value,
            RequestFingerprintEncoder.Compute("consumer-a", spaced).Value);
    }

    [Fact]
    public void AValueCannotForgeAnotherMembersBoundary()
    {
        // Both pairs concatenate to the same naive string; the length prefix keeps them apart.
        var one = Request(templateId: "a", version: "b\t1\tc");
        var other = Request(templateId: "a\t3\tb", version: "c");

        Assert.NotEqual(
            RequestFingerprintEncoder.Compute("consumer-a", one).Value,
            RequestFingerprintEncoder.Compute("consumer-a", other).Value);
    }

    [Fact]
    public void ValuesWithTabsLineBreaksAndMultiByteCharactersAreFingerprinted()
    {
        var one = Request(profile: "pro\tfile");
        var other = Request(profile: "pro\nfile");
        var accented = Request(profile: "prôfile");

        Assert.NotEqual(
            RequestFingerprintEncoder.Compute("consumer-a", one).Value,
            RequestFingerprintEncoder.Compute("consumer-a", other).Value);
        Assert.NotEqual(
            RequestFingerprintEncoder.Compute("consumer-a", one).Value,
            RequestFingerprintEncoder.Compute("consumer-a", accented).Value);
    }

    [Fact]
    public void TheEncoderTakesNothingButTheConsumerAndTheRequest()
    {
        // The encoder's only computation names the consumer and the request: no baseline, no
        // store, no clock and no resolved version can reach the identity.
        var compute = Assert.Single(
            typeof(RequestFingerprintEncoder).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.Name == nameof(RequestFingerprintEncoder.Compute));

        var parameters = compute.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal(typeof(CreateReportJobRequest), parameters[1].ParameterType);
        Assert.Empty(typeof(RequestFingerprintEncoder).GetConstructors());
        Assert.All(
            typeof(RequestFingerprintEncoder).GetFields(BindingFlags.Public | BindingFlags.Static),
            field => Assert.True(field.IsLiteral || field.IsInitOnly, "the encoder holds no mutable state"));
    }

    private static CreateReportJobRequest Request(
        string? version = "1.0.0",
        string uploadId = Upload,
        string profile = Profile,
        string templateId = Template,
        IReadOnlyList<string>? outputs = null,
        string? culture = null,
        string? timeZone = null) =>
        new(templateId, version, uploadId, profile, outputs ?? ["pdf"], culture, timeZone);

    private static async Task<CreateReportJobRequest> ReadAsync(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        context.Request.ContentType = CreateReportJobRequestReader.JsonMediaType;
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);

        var result = await CreateReportJobRequestReader.ReadAsync(context.Request, TestContext.Current.CancellationToken);
        return Assert.IsType<CreateReportJobRequestReader.Result.Accepted>(result).Request;
    }
}
