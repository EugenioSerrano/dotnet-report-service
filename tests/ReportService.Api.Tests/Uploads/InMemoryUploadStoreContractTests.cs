using ReportService.Api.Uploads;
using ReportService.Uploads;
using ReportService.Testing;

namespace ReportService.Api.Tests.Uploads;

/// <summary>The upload store contract, run against the in-memory store.</summary>
public sealed class InMemoryUploadStoreContractTests : UploadStoreContract
{
    private readonly InMemoryUploadStore _store = new();

    /// <summary>The store this class's cases run against.</summary>
    protected override Task<IUploadStore> CreateStoreAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IUploadStore>(_store);
}
