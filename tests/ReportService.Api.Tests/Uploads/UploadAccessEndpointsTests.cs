using System.Net;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Uploads;
using ReportService.Storage;
using ReportService.Uploads;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// Who may use an upload, through the API's real pipeline: the declared consumer is
/// resolved against the registry before the body is read and before any record is
/// touched, so an unknown identity learns nothing and leaves nothing behind, an
/// inactive consumer keeps the uploads it already has, and another consumer's upload
/// is answered exactly as one that does not exist.
/// </summary>
public sealed class UploadAccessEndpointsTests
{
    private static readonly (string ConsumerId, bool Active)[] Consumers =
        [("dev-consumer", true), ("other-consumer", true), ("dev-consumer-disabled", false)];

    private const string MissingUpload = "upload-44444444444444444444444444444444";

    [Fact]
    public async Task AnUnknownIdentityIsAnsweredTheSameWayOnEveryOperation()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        var baseline = await AnswerAsync(host, null, uploadId);

        foreach (var declared in new string?[] { null, string.Empty, "two-values, joined", "someone-else" })
        {
            var answer = await AnswerAsync(host, declared, uploadId);

            Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
            Assert.Equal(ErrorCodes.ConsumerUnknown, answer.Code);
            Assert.Equal("Unknown consumer", answer.Title);
            Assert.Equal(UploadEndpoints.ConsumerUnknownDetail, answer.Detail);
            Assert.Equal(baseline.Problem, answer.Problem);
        }
    }

    [Fact]
    public async Task TheSameAnswerIsGivenForAnUploadThatDoesNotExist()
    {
        using var host = new UploadApiHost(Consumers);
        var existing = await host.CreateAsync();
        var callsBefore = Calls(host.Store);

        var onExisting = await AnswerAsync(host, "someone-else", existing);
        var onAbsent = await AnswerAsync(host, "someone-else", MissingUpload);

        Assert.Equal(HttpStatusCode.Forbidden, onExisting.Status);
        Assert.Equal(HttpStatusCode.Forbidden, onAbsent.Status);
        Assert.Equal(onExisting.Problem, onAbsent.Problem);

        // The gate answers before any record is touched, for an existing upload and for an absent one alike.
        Assert.Equal(callsBefore, Calls(host.Store));
    }

    [Fact]
    public async Task AnUnknownIdentityIsRefusedBeforeTheBodyIsRead()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        var callsBefore = Calls(host.Store);
        using var create = await host.Client.SendAsync(
            UploadApiHost.CreateRequest("someone-else", "not a document"), TestContext.Current.CancellationToken);
        using var status = await host.Client.SendAsync(
            UploadApiHost.StatusRequest(uploadId, "someone-else"), TestContext.Current.CancellationToken);
        using var content = await host.Client.SendAsync(
            UploadApiHost.ContentRequest(uploadId, "someone-else", body: "not a document"), TestContext.Current.CancellationToken);
        using var confirm = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(uploadId, "someone-else", """{"sha256":"not-a-hash"}"""), TestContext.Current.CancellationToken);

        Assert.All([create, status, content, confirm], response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        Assert.Equal(callsBefore, Calls(host.Store));
    }

    [Fact]
    public async Task AnInactiveConsumerCannotOpenAnUpload()
    {
        using var host = new UploadApiHost(Consumers);

        using var refused = await host.Client.SendAsync(
            UploadApiHost.CreateRequest("dev-consumer-disabled", "not a document"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var answer = await UploadApiHost.ProblemAsync(refused);
        Assert.Contains(ErrorCodes.ConsumerInactive, answer, StringComparison.Ordinal);
        Assert.Contains("Inactive consumer", answer, StringComparison.Ordinal);
        Assert.Contains(ConsumerAccess.InactiveDetail, answer, StringComparison.Ordinal);
        Assert.Empty(host.Store.Records);
        Assert.Equal(0, Calls(host.Store));
    }

    [Fact]
    public async Task AnInactiveConsumerKeepsItsStatusContentAndConfirmation()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        var created = await ReadAsync(host, uploadId);

        host.ReplaceRegistry(("dev-consumer", false), ("other-consumer", true));

        // The registry change is applied by the running service, without a restart.
        await UploadApiHost.UntilAsync(
            "the registry change to be applied",
            async token => (await host.Client.SendAsync(UploadApiHost.CreateRequest("dev-consumer"), token)).StatusCode
                == HttpStatusCode.Forbidden);

        using var status = await host.Client.SendAsync(
            UploadApiHost.StatusRequest(uploadId, "dev-consumer"), TestContext.Current.CancellationToken);
        using var content = await host.Client.SendAsync(
            UploadApiHost.ContentRequest(uploadId, "dev-consumer"), TestContext.Current.CancellationToken);
        using var confirm = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(created, await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, confirm.StatusCode);

        var record = await host.Store.FindAsync(uploadId, "dev-consumer", TestContext.Current.CancellationToken);
        Assert.Equal(UploadStatus.Verifying, record!.Status);
    }

    [Fact]
    public async Task AConsumerRemovedByARegistryChangeLosesEveryOperationAndGainsThemBackWhenReadmitted()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();

        host.ReplaceRegistry(("other-consumer", true), ("dev-consumer-disabled", false));
        await UploadApiHost.UntilAsync(
            "the removal to be applied",
            async token => (await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), token)).StatusCode
                == HttpStatusCode.Forbidden);

        using var status = await host.Client.SendAsync(
            UploadApiHost.StatusRequest(uploadId, "dev-consumer"), TestContext.Current.CancellationToken);
        using var content = await host.Client.SendAsync(
            UploadApiHost.ContentRequest(uploadId, "dev-consumer"), TestContext.Current.CancellationToken);
        using var confirm = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(uploadId, "dev-consumer"), TestContext.Current.CancellationToken);
        Assert.All([status, content, confirm], response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));

        host.ReplaceRegistry(Consumers);
        await UploadApiHost.UntilAsync(
            "the readmission to be applied",
            async token => (await host.Client.SendAsync(UploadApiHost.StatusRequest(uploadId, "dev-consumer"), token)).StatusCode
                == HttpStatusCode.OK);

        using var readmitted = await host.Client.SendAsync(
            UploadApiHost.StatusRequest(uploadId, "dev-consumer"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, readmitted.StatusCode);
    }

    [Fact]
    public async Task AnotherConsumersUploadIsAnsweredExactlyAsAnAbsentOne()
    {
        using var host = new UploadApiHost(Consumers);
        var uploadId = await host.CreateAsync();
        using var sent = await host.Client.SendAsync(
            UploadApiHost.ContentRequest(uploadId, "dev-consumer"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var before = await host.Store.FindAsync(uploadId, "dev-consumer", TestContext.Current.CancellationToken);

        using var absentStatus = await host.Client.SendAsync(
            UploadApiHost.StatusRequest(MissingUpload, "other-consumer"), TestContext.Current.CancellationToken);
        using var foreignStatus = await host.Client.SendAsync(
            UploadApiHost.StatusRequest(uploadId, "other-consumer"), TestContext.Current.CancellationToken);
        using var absentContent = await host.Client.SendAsync(
            UploadApiHost.ContentRequest(MissingUpload, "other-consumer"), TestContext.Current.CancellationToken);
        using var foreignContent = await host.Client.SendAsync(
            UploadApiHost.ContentRequest(uploadId, "other-consumer"), TestContext.Current.CancellationToken);
        using var absentConfirm = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(MissingUpload, "other-consumer"), TestContext.Current.CancellationToken);
        using var foreignConfirm = await host.Client.SendAsync(
            UploadApiHost.ConfirmRequest(uploadId, "other-consumer"), TestContext.Current.CancellationToken);

        foreach (var (absent, foreign) in new[] { (absentStatus, foreignStatus), (absentContent, foreignContent), (absentConfirm, foreignConfirm) })
        {
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.Equal(await UploadApiHost.ProblemAsync(absent), await UploadApiHost.ProblemAsync(foreign));
        }

        var after = await host.Store.FindAsync(uploadId, "dev-consumer", TestContext.Current.CancellationToken);
        Assert.Equal(before, after);
        var staging = await host.Objects.GetInfoAsync(
            ObjectKey.FromSegments("staging", uploadId, "content"), TestContext.Current.CancellationToken);
        Assert.True(staging.Succeeded, "the upload's own object had to be left where it was");
    }

    private static async Task<(HttpStatusCode Status, string? Code, string? Title, string? Detail, string Problem)> AnswerAsync(
        UploadApiHost host,
        string? declared,
        string uploadId)
    {
        using var response = await host.Client.SendAsync(
            UploadApiHost.StatusRequest(uploadId, declared), TestContext.Current.CancellationToken);
        using var body = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = body.RootElement;
        return (
            response.StatusCode,
            root.TryGetProperty("code", out var code) ? code.GetString() : null,
            root.TryGetProperty("title", out var title) ? title.GetString() : null,
            root.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
            await UploadApiHost.ProblemAsync(response));
    }

    private static int Calls(InMemoryUploadStore store) => store.CreateCalls + store.FindCalls;

    private static async Task<string> ReadAsync(UploadApiHost host, string uploadId)
    {
        using var response = await host.Client.SendAsync(
            UploadApiHost.StatusRequest(uploadId, "dev-consumer"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
