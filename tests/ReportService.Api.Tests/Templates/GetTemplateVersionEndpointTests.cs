using System.Net;
using System.Text.Json;
using ReportService.Api.Http;
using ReportService.Api.Templates;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;

namespace ReportService.Api.Tests.Templates;

/// <summary>
/// The owner's read of a taken template version: the answer carries what the take
/// fixed and nothing about how the copy is kept; a version that was never taken and
/// another consumer's version are answered with the one same problem; a name that
/// cannot be a taken version is answered without reading the store; and a request
/// the service does not serve never reaches it.
/// </summary>
public sealed class GetTemplateVersionEndpointTests
{
    [Fact]
    public async Task TheOwnerReadsWhatTheTakeFixedAboutItsVersion()
    {
        using var host = new TemplateVersionApiHost();
        var record = host.Seed();

        using var response = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest(record.TemplateId, record.Version, TemplateVersionApiHost.Owner),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var answer = await AnswerAsync(response);
        var members = answer.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            [
                "commitSha", "engine", "engineVersion", "owner", "packageHash", "shared", "takenAt", "templateId", "version",
            ],
            members);
        Assert.Equal("invoice-energy", answer.GetProperty("templateId").GetString());
        Assert.Equal("1.1.0", answer.GetProperty("version").GetString());
        Assert.Equal(TemplateVersionRecords.CommitSha, answer.GetProperty("commitSha").GetString());
        Assert.Equal("sha256:" + TemplateVersionRecords.PackageSha256, answer.GetProperty("packageHash").GetString());
        Assert.Equal("SCRIBAN", answer.GetProperty("engine").GetString());
        Assert.Equal("7.5.1", answer.GetProperty("engineVersion").GetString());
        Assert.Equal(TemplateVersionApiHost.Owner, answer.GetProperty("owner").GetString());
        Assert.False(answer.GetProperty("shared").GetBoolean());
        Assert.Equal(1, host.Store.FindCalls);
    }

    [Fact]
    public async Task TheAnswerCarriesTheTakeInstantInTheApisOneTimestampFormat()
    {
        using var host = new TemplateVersionApiHost();
        host.Seed();

        using var response = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest("invoice-energy", "1.1.0", TemplateVersionApiHost.Owner),
            TestContext.Current.CancellationToken);

        var answer = await AnswerAsync(response);
        Assert.Equal("2026-11-01T12:00:00.123Z", answer.GetProperty("takenAt").GetString());
    }

    [Fact]
    public async Task AVeryLongVersionNameIsAnsweredAsNotFoundWithoutReadingTheStore()
    {
        using var host = new TemplateVersionApiHost();

        // 129 characters: a semantic version is at most 128, so it cannot be a taken version's name.
        var tooLong = "1.0.0+" + new string('a', 123);
        using var response = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest("invoice-energy", tooLong, TemplateVersionApiHost.Owner),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Store.FindCalls);
    }

    [Fact]
    public async Task AVeryLongIdentifierIsAnsweredAsNotFoundWithoutReadingTheStore()
    {
        using var host = new TemplateVersionApiHost();

        // 129 characters: an identifier is at most 128, so it cannot be a taken version's.
        using var response = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest(new string('a', 129), "1.1.0", TemplateVersionApiHost.Owner),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Store.FindCalls);
    }

    [Theory]
    [InlineData("Invoice-Energy")]
    [InlineData("invoice energy")]
    [InlineData("invoice/energy")]
    [InlineData("invoice_energy.")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task ANameThatCannotBeATemplateIdentifierIsAnsweredAsNotFoundWithoutReadingTheStore(string templateId)
    {
        using var host = new TemplateVersionApiHost();

        using var response = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest(templateId, "1.1.0", TemplateVersionApiHost.Owner),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Store.FindCalls);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0 -pre")]
    public async Task AVersionThatIsNotASemanticVersionIsAnsweredAsNotFoundWithoutReadingTheStore(string version)
    {
        using var host = new TemplateVersionApiHost();

        using var response = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest("invoice-energy", version, TemplateVersionApiHost.Owner),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Store.FindCalls);
    }

    [Fact]
    public async Task AVersionThatWasNeverTakenIsAnsweredAsNotFound()
    {
        using var host = new TemplateVersionApiHost();

        using var response = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest("invoice-energy", "1.1.0", TemplateVersionApiHost.Owner),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.TemplateNotFound, await CodeAsync(response));
        Assert.Equal(1, host.Store.FindCalls);
    }

    [Fact]
    public async Task AnotherConsumersVersionIsAnsweredExactlyLikeOneThatWasNeverTaken()
    {
        using var host = new TemplateVersionApiHost();
        var record = host.Seed(owner: TemplateVersionApiHost.Owner);

        using var foreign = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest(record.TemplateId, record.Version, TemplateVersionApiHost.Other),
            TestContext.Current.CancellationToken);
        using var absent = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest("invoice-energy", "2.0.0", TemplateVersionApiHost.Other),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal(await TemplateVersionApiHost.ProblemAsync(absent), await TemplateVersionApiHost.ProblemAsync(foreign));
        Assert.Equal("Template not found", await TitleAsync(foreign));
        Assert.Equal("The template version was not found.", await DetailAsync(foreign));
    }

    [Fact]
    public async Task ARegisteredConsumerThatWasMadeInactiveMayStillReadItsVersion()
    {
        using var host = new TemplateVersionApiHost();
        var record = host.Seed(owner: TemplateVersionApiHost.Inactive);

        using var response = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest(record.TemplateId, record.Version, TemplateVersionApiHost.Inactive),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a consumer")]
    [InlineData("unknown-system")]
    public async Task AReaderThatIsNotRegisteredIsRefusedBeforeTheStoreIsRead(string? consumerId)
    {
        using var host = new TemplateVersionApiHost();
        host.Seed();

        using var response = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest("invoice-energy", "1.1.0", consumerId),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ConsumerUnknown, await CodeAsync(response));
        Assert.Equal(0, host.Store.FindCalls);
    }

    [Fact]
    public async Task AStoreThatDoesNotAnswerIsARetryableServiceCondition()
    {
        var store = new UnavailableTemplateVersionStore();
        using var host = new TemplateVersionApiHost(store);

        using var response = await host.Client.SendAsync(
            TemplateVersionApiHost.ReadRequest("invoice-energy", "1.1.0", TemplateVersionApiHost.Owner),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ErrorCodes.ServiceUnavailable, await CodeAsync(response));
        Assert.Equal(1, store.FindCalls);

        // The cause stays inside the service: the answer names no exception, connection or SQL state.
        var problem = await TemplateVersionApiHost.ProblemAsync(response);
        Assert.DoesNotContain("TemplateStoreUnavailableException", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("store did not answer", problem, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> AnswerAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string?> MemberAsync(HttpResponseMessage response, string name) =>
        (await AnswerAsync(response)).GetProperty(name).GetString();

    private static Task<string?> CodeAsync(HttpResponseMessage response) => MemberAsync(response, "code");

    private static Task<string?> TitleAsync(HttpResponseMessage response) => MemberAsync(response, "title");

    private static Task<string?> DetailAsync(HttpResponseMessage response) => MemberAsync(response, "detail");

    // The store a service cannot reach: the read fails, every other rule of the endpoint is untouched.
    private sealed class UnavailableTemplateVersionStore : ITemplateVersionStore
    {
        public int FindCalls { get; private set; }

        public Task<TemplateVersionRecord?> FindAsync(string templateId, string version, CancellationToken cancellationToken)
        {
            FindCalls++;
            throw new TemplateStoreUnavailableException(new InvalidOperationException("the server is not there"));
        }

        public Task<TemplateVersionInsert> TryInsertAsync(TemplateVersionRecord record, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("this store is only read");
    }
}
