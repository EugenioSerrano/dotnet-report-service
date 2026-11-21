using ReportService.Templates;

namespace ReportService.Rendering;

/// <summary>
/// A change applied to the composition <em>before</em> the expected text units are
/// extracted, so the reference the checks compare against is itself wrong. It
/// exists for the known-bad controls of the rendering suite only: production code
/// never registers an implementation, and no public member accepts one. A row
/// removed, a row duplicated, a row that prints no text, an anchor truncated or
/// encoded twice, a header anchor dropped.
/// </summary>
internal interface IComposedBlockMutation
{
    /// <summary>The name the rendering host selects the control by.</summary>
    string Name { get; }

    /// <summary>Returns the mutated composition.</summary>
    ComposedBlock Apply(ComposedBlock block);
}

/// <summary>
/// A change applied to the composed HTML <em>after</em> the expected text units are
/// extracted and before printing, so the reference stays right and the printed
/// document is wrong. It exists for the known-bad controls of the rendering suite
/// only: production code never registers an implementation, and no public member
/// accepts one. A word dropped, a line repeated, a row removed, a line reordered.
/// </summary>
internal interface IComposedHtmlMutation
{
    /// <summary>The name the rendering host selects the control by.</summary>
    string Name { get; }

    /// <summary>Returns the mutated HTML.</summary>
    string Apply(string html);
}
