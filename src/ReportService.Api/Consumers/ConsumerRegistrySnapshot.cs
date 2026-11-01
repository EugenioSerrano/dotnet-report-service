using System.Security.Cryptography;
using System.Text;

namespace ReportService.Api.Consumers;

/// <summary>One consumer of the registry: an identity and whether it is active, with optional descriptive fields.</summary>
/// <param name="ConsumerId">The consumer's identity, unique within the registry.</param>
/// <param name="DisplayName">An optional human-readable name.</param>
/// <param name="Active">Whether the consumer is active.</param>
/// <param name="AuthenticationSubject">Optional; carried through unchanged and not used yet.</param>
/// <param name="CreatedAt">Optional; carried through unchanged and not used yet.</param>
public sealed record ConsumerEntry(string ConsumerId, string? DisplayName, bool Active, string? AuthenticationSubject, string? CreatedAt);

/// <summary>
/// An immutable, validated registry. A request reads one snapshot and uses it
/// throughout; a change publishes a new snapshot and never alters this one.
/// </summary>
public sealed class ConsumerRegistrySnapshot
{
    private ConsumerRegistrySnapshot(IReadOnlyList<ConsumerEntry> entries)
    {
        Entries = entries;
        Fingerprint = ComputeFingerprint(entries);
    }

    /// <summary>The entries, in registry-file order.</summary>
    public IReadOnlyList<ConsumerEntry> Entries { get; }

    /// <summary>The SHA-256 of the identities and active states; what a change moves, and what the diagnostic reports.</summary>
    public string Fingerprint { get; }

    /// <summary>Creates a snapshot of already validated entries.</summary>
    public static ConsumerRegistrySnapshot Create(IReadOnlyList<ConsumerEntry> entries) => new([.. entries]);

    /// <summary>
    /// Lowercase hexadecimal SHA-256 over the canonical form: one line
    /// <c>consumerId TAB true|false</c> per entry, sorted by identity (ordinal),
    /// joined with <c>\n</c>, UTF-8.
    /// </summary>
    public static string ComputeFingerprint(IEnumerable<ConsumerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var canonical = string.Join(
            '\n',
            entries.OrderBy(e => e.ConsumerId, StringComparer.Ordinal).Select(e => $"{e.ConsumerId}\t{(e.Active ? "true" : "false")}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
