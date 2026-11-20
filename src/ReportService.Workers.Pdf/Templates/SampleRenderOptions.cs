using ReportService.ServiceDefaults;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>
/// The settings of the take's sample render, in the <c>Templates:Sample</c> section:
/// the deadline handed to each of the sample's two render calls. A call that passes
/// it is not a refusal of the version - it is <c>RENDER_TIMEOUT</c>, retried inside
/// the activity's three attempts - so the budget sits below the take's own deadline
/// by design and is validated at start.
/// </summary>
public sealed class SampleRenderOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "Templates:Sample";

    /// <summary>The shortest budget one sample render call may carry, in seconds.</summary>
    public const int MinRenderBudgetSeconds = 1;

    /// <summary>The longest budget one sample render call may carry, in seconds: the arithmetic of the plan's E.3 admits no more.</summary>
    public const int MaxRenderBudgetSeconds = 22;

    /// <summary>The deadline of each of the two render calls, in seconds; the shipped default and the ceiling are E.3's.</summary>
    public int RenderBudgetSeconds { get; set; } = 20;

    /// <summary>The deadline of one render call.</summary>
    public TimeSpan RenderBudget => TimeSpan.FromSeconds(RenderBudgetSeconds);

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (RenderBudgetSeconds < MinRenderBudgetSeconds || RenderBudgetSeconds > MaxRenderBudgetSeconds)
        {
            yield return new ConfigurationProblem(
                nameof(RenderBudgetSeconds),
                $"is not between {MinRenderBudgetSeconds} and {MaxRenderBudgetSeconds} seconds");
        }
    }
}
