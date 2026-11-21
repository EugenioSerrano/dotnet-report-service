using ReportService.TemplateChecks.Sample;

namespace ReportService.Testing.TemplateChecks;

/// <summary>
/// The sample renderer a hermetic test hands the check: it answers with what the test
/// scripted, in order, and remembers every request, so a case asserts what the check
/// asked for (the version's package, its own sample bytes, the empty second call)
/// without a browser, a PDF reader or an engine. It holds no product behaviour.
/// </summary>
public sealed class InMemorySampleRenderer : ISampleRenderer
{
    /// <summary>The facts a rendered answer carries when a case does not care about them.</summary>
    public static SampleFacts Facts { get; } = new(
        Pages: 1,
        HtmlBytes: 128,
        PdfBytes: 256,
        Words: 7,
        ComposeMs: 1.5,
        PrintMs: 2.5,
        VerifyMs: 3.5);

    private readonly Queue<Func<SampleRequest, SampleAttempt>> _answers = new();

    private Func<SampleRequest, SampleAttempt>? _every;

    /// <summary>Every request the check made, in order.</summary>
    public List<SampleRequest> Requests { get; } = [];

    /// <summary>Scripts one answer for the next request, in the order the calls are made.</summary>
    /// <param name="answer">The answer, from the request.</param>
    public InMemorySampleRenderer Answering(Func<SampleRequest, SampleAttempt> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answers.Enqueue(answer);
        return this;
    }

    /// <summary>Scripts the same answer for every request.</summary>
    /// <param name="answer">The answer, from the request.</param>
    public InMemorySampleRenderer Always(Func<SampleRequest, SampleAttempt> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _every = answer;
        return this;
    }

    /// <summary>Scripts a rendered answer with <see cref="Facts"/> for every request.</summary>
    public InMemorySampleRenderer AlwaysRendered() => Always(_ => new SampleAttempt.Rendered(Facts));

    /// <summary>Scripts the same failure for every request.</summary>
    /// <param name="failure">The failure to answer with.</param>
    public InMemorySampleRenderer AlwaysFailed(SampleFailure failure) => Always(_ => new SampleAttempt.Failed(failure));

    /// <summary>Scripts the same unavailability for every request.</summary>
    /// <param name="cause">The cause to answer with.</param>
    public InMemorySampleRenderer AlwaysUnavailable(SampleUnavailableCause cause) =>
        Always(_ => new SampleAttempt.Unavailable(cause));

    /// <inheritdoc />
    public Task<SampleAttempt> RenderAsync(SampleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);

        var answer = _answers.Count > 0 ? _answers.Dequeue() : _every;
        if (answer is null)
        {
            throw new InvalidOperationException("The case scripted no answer for this sample render.");
        }

        return Task.FromResult(answer(request));
    }
}
