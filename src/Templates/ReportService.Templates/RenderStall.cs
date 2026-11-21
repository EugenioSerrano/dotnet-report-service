namespace ReportService.Templates;

/// <summary>The phase of a render a test-only stall can be seated in.</summary>
internal enum RenderPhase
{
    /// <summary>The composition of the block's HTML, after the guard's clock has started.</summary>
    Composition,

    /// <summary>The print step, before the printer is asked for a document.</summary>
    Print,
}

/// <summary>
/// A stall a test seats in a render, so the composition guard or the worker's watchdog
/// has something honest to stop. It exists for the deadline cases only: production code
/// never registers an implementation, no public member accepts one, and the worker that
/// would run the container is not granted this assembly's internals. The seam carries no
/// duration - its call sites are production code and cannot know a test's span, so the
/// implementation owns it - and an implementation deliberately does not observe the
/// cancellation token: a stall that could be cancelled would stop being "neither writes
/// nor returns".
/// </summary>
internal interface IRenderStall
{
    /// <summary>Waits at <paramref name="phase"/> for the span the implementation owns.</summary>
    /// <param name="phase">The phase the stall is seated in.</param>
    /// <param name="cancellationToken">The step's cancellation token, passed unchanged.</param>
    Task StallAsync(RenderPhase phase, CancellationToken cancellationToken);
}
