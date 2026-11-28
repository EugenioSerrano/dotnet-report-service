using ReportService.Templates;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>One trap the tool looks for in a package that rendered.</summary>
public interface IWarningRule
{
    /// <summary>The warnings this rule finds in <paramref name="input"/>.</summary>
    /// <param name="input">The rendered package: its composed preview, its sources and its sample.</param>
    IEnumerable<AuthoringWarning> Find(WarningInput input);
}

/// <summary>What the rules read: nothing they could change, and nothing outside the package and its sample.</summary>
/// <param name="Engine">The engine's manifest name.</param>
/// <param name="Composed">The composed preview block, with its fragments.</param>
/// <param name="Sources">Every template source of the package, by package-relative path.</param>
/// <param name="Header">The sample's header as the render context reads it.</param>
/// <param name="Records">The sample's records as the render context reads them.</param>
public sealed record WarningInput(
    string Engine,
    ComposedBlock Composed,
    IReadOnlyList<KeyValuePair<string, string>> Sources,
    IReadOnlyDictionary<string, object?> Header,
    IReadOnlyList<ComposedRecord> Records);
