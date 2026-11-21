namespace ReportService.Data;

/// <summary>
/// One value the reader yielded: the header, or one record of the snapshot, with the
/// bytes it occupies exactly as the snapshot held them and the position it started
/// at. Ordinal 0 is the header; records are numbered from 1 in read order. Nothing in
/// it has been parsed, rounded or rewritten, so whatever the partitioner writes back
/// is the producer's own text.
/// </summary>
/// <param name="Ordinal">0 for the header, otherwise the record's 1-based position.</param>
/// <param name="Json">The value's own JSON, byte for byte as the snapshot held it.</param>
/// <param name="ByteLength">How many bytes of the document the value occupies.</param>
/// <param name="Offset">Where the value starts, in bytes of the uncompressed document.</param>
public sealed record RawRecord(int Ordinal, ReadOnlyMemory<byte> Json, long ByteLength, long Offset);
