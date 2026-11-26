namespace ReportService.Data.Partitioning;

/// <summary>
/// The durable checkpoint of a partition pass: what a resumed pass needs
/// to continue from the last committed block without rewriting it. It is a value the
/// caller persists — the Temporal activity carries it in and out of the heartbeat details
/// — not a store of its own; the committed block objects and the committed index entries
/// it names are the durable committed-block record. A block counts as
/// committed only once its index entry, with its SHA-256, is durable.
/// </summary>
/// <param name="LastCommittedBlockIndex">The index of the last durably committed block.</param>
/// <param name="LastRecordOrdinal">The ordinal of the last record held by the committed blocks; 0 when none.</param>
/// <param name="CarriedForward">The running sum of the amounts of every committed record, where the next block opens.</param>
/// <param name="NextContinuesGroup">Whether the next block continues a group carried out of the last committed block.</param>
/// <param name="NextOpeningGroupKey">The group key the next block opens with when it continues a group; null otherwise.</param>
/// <param name="CommittedBlocks">The durable index entries of blocks 0..<see cref="LastCommittedBlockIndex"/>, in order.</param>
/// <param name="NextOpeningGroupAccumulated">The open group's sum over the committed blocks when the next block continues it; null otherwise.</param>
public sealed record PartitionCheckpoint(
    int LastCommittedBlockIndex,
    int LastRecordOrdinal,
    decimal CarriedForward,
    bool NextContinuesGroup,
    string? NextOpeningGroupKey,
    IReadOnlyList<BlockIndexEntry> CommittedBlocks,
    decimal? NextOpeningGroupAccumulated = null);
