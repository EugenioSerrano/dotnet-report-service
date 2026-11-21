using ReportService.TemplatePackages.Records;
using ReportService.Testing;

namespace ReportService.Api.Tests.Templates;

/// <summary>
/// The in-memory template-version store held to the contract the real store must
/// also keep, so the doubles the endpoint and the later take read through can never
/// drift from what the table does: the first insert of a version is the record that
/// stays, and a second insert answers it.
/// </summary>
public sealed class InMemoryTemplateVersionStoreContractTests : TemplateVersionStoreContract
{
    /// <inheritdoc />
    protected override ITemplateVersionStore Store { get; } = new InMemoryTemplateVersionStore();
}
