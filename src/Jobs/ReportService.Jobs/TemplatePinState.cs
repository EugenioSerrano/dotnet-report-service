using ReportService.TemplatePackages.Records;

namespace ReportService.Jobs;

/// <summary>
/// What writing a job's template pin left the record saying: the pin this call
/// committed, the pin an earlier attempt committed first, or the job having ended
/// before any pin could be written. A pin is written once, so the first committed
/// answer is the one every later attempt receives, whatever it read on its own.
/// </summary>
public sealed class TemplatePinState
{
    private TemplatePinState(TemplatePin? pin, bool wasWrittenByThisCall, bool jobTerminal)
    {
        Pin = pin;
        WasWrittenByThisCall = wasWrittenByThisCall;
        JobTerminal = jobTerminal;
    }

    /// <summary>The pin the record holds; null only when the job had already ended.</summary>
    public TemplatePin? Pin { get; }

    /// <summary>True when this call wrote the pin; false when it was already there.</summary>
    public bool WasWrittenByThisCall { get; }

    /// <summary>True when the job had ended and nothing was written.</summary>
    public bool JobTerminal { get; }

    /// <summary>This call committed <paramref name="pin"/>.</summary>
    /// <param name="pin">The pin that was written.</param>
    public static TemplatePinState Pinned(TemplatePin pin) => new(pin, true, false);

    /// <summary>An earlier attempt had already committed <paramref name="pin"/>; nothing was written.</summary>
    /// <param name="pin">The pin the record holds.</param>
    public static TemplatePinState AlreadyPinned(TemplatePin pin) => new(pin, false, false);

    /// <summary>The job had ended, so no pin was written.</summary>
    public static TemplatePinState JobEnded() => new(null, false, true);
}
