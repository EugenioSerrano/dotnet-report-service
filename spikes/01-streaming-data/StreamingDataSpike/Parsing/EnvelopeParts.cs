namespace StreamingDataSpike.Parsing;

/// <summary>The envelope header, materialised once.</summary>
/// <param name="Values">The header object.</param>
/// <param name="ByteLength">Size of the header object in the input, in bytes.</param>
public sealed record EnvelopeHeader(Dictionary<string, object?> Values, long ByteLength);

/// <summary>One record of the <c>items</c> array.</summary>
/// <param name="Ordinal">1-based position in the array.</param>
/// <param name="Values">The record object.</param>
/// <param name="ByteLength">Size of the record object in the input, in bytes.</param>
/// <param name="ByteOffset">Offset of the record in the uncompressed input.</param>
public sealed record EnvelopeRecord(long Ordinal, Dictionary<string, object?> Values, long ByteLength, long ByteOffset);
