namespace ReportService.EndToEndTests;

/// <summary>
/// The READY condition over the real composition: a job over the configured
/// single-block record limit whose partition is not committed still ends
/// <c>QUOTA_EXCEEDED</c> — the data step refuses it before any block renders, no
/// render, assemble or publish step is scheduled, no part commit and no artifact
/// exists. The temporary MVP refusal is untouched by the multi-block stage.
/// Authored in the V-Bounce; run in the e2e cycle.
/// </summary>
public sealed class SingleBlockLimitStillActiveEndToEndTests
{
    [Fact]
    public async Task AJobOverTheSingleBlockLimitStillEndsQuotaExceededWithNoPartOrArtifact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var stage = new MultiBlockStage();
        await using var composition = await stage.StartAsync(cancellationToken);
        await stage.TakeTemplateAsync(composition, cancellationToken);
        var uploadId = await stage.SeedReadyUploadAsync(composition, cancellationToken);
        var reportJobId = await stage.SubmitAsync(composition, uploadId, cancellationToken);

        var ended = await stage.WaitForEndAsync(composition, reportJobId, cancellationToken);

        Assert.Equal("FAILED", ended.Status);
        Assert.Equal("QUOTA_EXCEEDED", ended.ErrorCode ?? ended.OutputErrorCode);
        Assert.Null(ended.ArtifactId);
        Assert.NotNull(ended.OutputId);
        Assert.Empty(await stage.ReadPartsAsync(composition, ended.OutputId!, cancellationToken));
    }
}
