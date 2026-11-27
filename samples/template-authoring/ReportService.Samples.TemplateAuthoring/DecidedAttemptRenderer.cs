using ReportService.TemplateChecks.Sample;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// A sample render that is already decided: it returns the attempt it was given.
/// It exists only so a refused render can be handed to the take's own
/// <see cref="SampleRenderCheck"/>, which builds the refuse sentence; the tool
/// never writes a refusal of its own.
/// </summary>
/// <param name="attempt">The attempt every call answers.</param>
public sealed class DecidedAttemptRenderer(SampleAttempt attempt) : ISampleRenderer
{
    private readonly SampleAttempt _attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));

    /// <summary>The attempt this renderer answers.</summary>
    public SampleAttempt Attempt => _attempt;

    /// <inheritdoc />
    public Task<SampleAttempt> RenderAsync(SampleRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_attempt);
    }
}
