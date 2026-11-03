namespace ReportService.TemplatePackages.Records;

/// <summary>
/// Whether a consumer may use a taken version. The whole rule is two clauses:
/// the version is shared, or it belongs to the consumer asking. Nothing else is
/// folded in â€” no case folding, no prefix, no registry lookup â€” because the
/// identifiers are the registry's exact ones and the owner is the value the
/// take read from the manifest. The by-name resolution and the metadata read
/// both call this predicate, so the two can never drift apart.
/// </summary>
public static class TemplateVersionVisibility
{
    /// <summary>True when <paramref name="record"/> may be used by <paramref name="requestingConsumerId"/>.</summary>
    /// <param name="record">The record of the taken version.</param>
    /// <param name="requestingConsumerId">The consumer asking, as the registry names it.</param>
    public static bool MayUse(TemplateVersionRecord record, string requestingConsumerId)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Shared
            || string.Equals(record.OwnerConsumerId, requestingConsumerId, StringComparison.Ordinal);
    }
}
