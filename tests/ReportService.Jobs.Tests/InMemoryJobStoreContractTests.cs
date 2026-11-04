using ReportService.Jobs;
using ReportService.Testing;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;

namespace ReportService.Jobs.Tests;

/// <summary>The job store contract, run against the in-memory store.</summary>
public sealed class InMemoryJobStoreContractTests : JobStoreContract
{
    private readonly InMemoryUploadStore _uploads = new();

    /// <summary>The taken versions a pinned case records, in memory like the store itself.</summary>
    protected override ITemplateVersionStore? Versions { get; } = new InMemoryTemplateVersionStore();

    /// <summary>The two stores this class's cases run against, both in memory.</summary>
    protected override Task<(IJobStore Jobs, IUploadStore Uploads)> CreateStoresAsync(CancellationToken cancellationToken) =>
        Task.FromResult<(IJobStore, IUploadStore)>((new InMemoryJobStore(_uploads), _uploads));
}
