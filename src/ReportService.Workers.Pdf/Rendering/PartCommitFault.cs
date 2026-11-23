namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// The test-only seam that makes a verified and streamed block fail before its part is
/// committed, so the attempt is retried and a second printed document really exists. It is
/// internal, no public member accepts one, and the production composition registers no
/// implementation: the only caller is the render step, and the only installer is a test host
/// this assembly names.
/// </summary>
internal interface PartCommitFault
{
    /// <summary>
    /// Fails the attempt of <paramref name="outputId"/>'s <paramref name="blockIndex"/> once,
    /// with an untyped exception, so the retry policy - never a typed refusal - decides.
    /// </summary>
    /// <param name="outputId">The output whose block is being committed.</param>
    /// <param name="blockIndex">The block's position in the partition.</param>
    /// <param name="cancellationToken">The step's cancellation token.</param>
    Task FailOnceAsync(string outputId, int blockIndex, CancellationToken cancellationToken);
}
