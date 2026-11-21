namespace ReportService.TemplateChecks.Sample;

/// <summary>
/// A render that could not complete, raised out of the check so the take fails
/// retryably instead of refusing the version: the check seam answers
/// <c>Pass</c> or <c>Refuse(code, reason)</c> only, so "the renderer was not up"
/// and "the render did not finish in time" travel as an exception. The cause is
/// the terminal code's: <c>RENDER_TIMEOUT</c> for a deadline, a crash's own
/// failure otherwise. It is never <c>TEMPLATE_INVALID</c>.
/// </summary>
public sealed class SampleRenderUnavailableException : Exception
{
    /// <summary>Creates the exception for the cause the render met.</summary>
    /// <param name="cause">A deadline trip or a crash.</param>
    /// <param name="part">The part that was rendering, when it is known.</param>
    public SampleRenderUnavailableException(SampleUnavailableCause cause, string? part = null)
        : base(MessageOf(cause, part))
    {
        Cause = cause;
        Part = part;
    }

    /// <summary>Why the render did not complete.</summary>
    public SampleUnavailableCause Cause { get; }

    /// <summary>The part that was rendering, when it is known.</summary>
    public string? Part { get; }

    private static string MessageOf(SampleUnavailableCause cause, string? part) =>
        cause == SampleUnavailableCause.RenderTimeout
            ? "the sample render did not finish within its deadline"
            : "the sample render could not run" + (part is null ? string.Empty : " at " + part);
}
