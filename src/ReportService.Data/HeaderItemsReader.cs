using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ReportService.Data;

/// <summary>
/// The forward-only reader of a snapshot document. It walks the document once, with
/// a buffer that starts small and grows by doubling up to the cap of a single token,
/// and materialises the header and then one record at a time: nothing here holds the
/// whole document or the whole record set. It yields the header first, with ordinal
/// 0, then each record in the order the snapshot holds it, with the bytes of the
/// value copied exactly as they arrived. Anything the profile refuses stops the pass
/// with a <see cref="DataContractViolation"/> that names the rule and the position,
/// never a value.
/// </summary>
public static class HeaderItemsReader
{
    /// <summary>The contract version the profile admits.</summary>
    public const string ContractVersion = "1.0";

    /// <summary>Reads <paramref name="source"/> as one snapshot document.</summary>
    /// <param name="source">The uncompressed document; a compressed snapshot is decompressed by the caller.</param>
    /// <param name="cancellationToken">The pass's cancellation token.</param>
    /// <returns>The header (ordinal 0) followed by the records (ordinal 1 and up).</returns>
    public static async IAsyncEnumerable<RawRecord> ReadAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var pass = new SnapshotPass();
        while (!pass.Finished)
        {
            var progressed = pass.Advance();
            if (pass.TryTake(out var value))
            {
                yield return value;
                continue;
            }

            if (pass.Finished)
            {
                break;
            }

            if (!progressed)
            {
                await pass.FillAsync(source, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One pass over one document: the buffer, the reader's state and the profile's position.</summary>
    private sealed class SnapshotPass
    {
        private readonly ArrayBufferWriter<byte> _captured = new(DataContractLimits.InitialBufferBytes);
        private byte[] _buffer = new byte[DataContractLimits.InitialBufferBytes];
        private int _filled;
        private int _consumedInBuffer;
        private int _passStart;
        private long _baseOffset;
        private long _consumedBeforeBuffer;
        private bool _endOfStream;
        private bool _finished;
        private JsonReaderState _state = new(new JsonReaderOptions { MaxDepth = DataContractLimits.Depth * 4 });
        private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

        // The names already read in each object open inside the value being captured:
        // one frame per object, and one byte buffer per captured value, so a name is
        // copied once as the value it has and a repeated one is met by its own bytes.
        private readonly NameFrame[] _frames = new NameFrame[DataContractLimits.Depth + 2];
        private byte[] _names = [];
        private int _namesLength;
        private int _frameTop;

        private RawRecord? _staged;
        private int _ordinal;

        // Where the profile is: which root property is expected, and what was seen.
        private Phase _phase = Phase.Root;
        private bool _versionSeen;
        private bool _headerSeen;
        private bool _itemsSeen;
        private bool _itemsStarted;
        private bool _bomHandled;

        // The value being captured.
        private bool _capturing;
        private bool _capturingHeader;
        private int _captureDepth;
        private long _captureStart;
        private bool _previousValueEnd;

        private enum Phase
        {
            Root,
            Properties,
            Version,
            Header,
            Items,
            Done,
        }

        // One object of the value being captured: the depth it opened at, and the names it
        // has read so far. The ranges and the lookup are kept between values and reused.
        private struct NameFrame
        {
            public int Depth;
            public int Count;
            public int[]? Ranges;
            public int[]? Slots;
            public int Mask;
        }

        public bool Finished => _finished;

        public bool TryTake([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RawRecord? value)
        {
            value = _staged;
            _staged = null;
            return value is not null;
        }

        public async ValueTask FillAsync(Stream source, CancellationToken cancellationToken)
        {
            Compact();
            if (_filled == _buffer.Length)
            {
                if (_buffer.Length >= DataContractLimits.BufferCapBytes)
                {
                    throw Refuse(DataContractViolation.QuotaExceeded, null, "token", _ordinal, Absolute(_consumedInBuffer));
                }

                var grown = new byte[Math.Min(DataContractLimits.BufferCapBytes, _buffer.Length * 2)];
                Buffer.BlockCopy(_buffer, 0, grown, 0, _filled);
                _buffer = grown;
            }

            var read = await source.ReadAsync(_buffer.AsMemory(_filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                _endOfStream = true;
                return;
            }

            _filled += read;

            // RFC 8259 lets a document start with a byte-order mark; exactly one is skipped,
            // and only where a document starts.
            if (!_bomHandled && _filled >= ByteOrderMark.Length + 1)
            {
                _bomHandled = true;
                if (_buffer.AsSpan(0, ByteOrderMark.Length).SequenceEqual(ByteOrderMark))
                {
                    Buffer.BlockCopy(_buffer, ByteOrderMark.Length, _buffer, 0, _filled - ByteOrderMark.Length);
                    _filled -= ByteOrderMark.Length;
                }
            }
        }

        public bool Advance()
        {
            if (_finished)
            {
                return false;
            }

            if (_filled == 0 && !_endOfStream)
            {
                return false;
            }

            _passStart = _consumedInBuffer;
            _baseOffset = _consumedBeforeBuffer + _consumedInBuffer;
            var reader = new Utf8JsonReader(
                _buffer.AsSpan(_consumedInBuffer, _filled - _consumedInBuffer), _endOfStream, _state);
            var progressed = false;
            try
            {
                while (reader.Read())
                {
                    progressed = true;
                    Handle(ref reader);
                    if (_staged is not null || _finished)
                    {
                        break;
                    }
                }
            }
            catch (JsonException)
            {
                // The root object already closed and something follows it: the document carries
                // content of its own after its end, which is never admitted.
                if (_phase == Phase.Done)
                {
                    throw Refuse(DataContractViolation.DataInvalid, "trailing-content", null, _ordinal, Absolute(reader.BytesConsumed));
                }

                // A document cut short fails only once its last bytes have been read; anything
                // the reader refuses while content still follows is not a document of the profile.
                throw Refuse(
                    DataContractViolation.DataInvalid,
                    _endOfStream ? "truncated" : "malformed",
                    null,
                    _capturing ? OrdinalOfCapture : _ordinal,
                    Absolute(reader.BytesConsumed));
            }

            _state = reader.CurrentState;
            _consumedInBuffer += (int)reader.BytesConsumed;

            if (!progressed && _endOfStream)
            {
                if (_phase == Phase.Done)
                {
                    _finished = true;
                    return true;
                }

                throw Refuse(DataContractViolation.DataInvalid, "truncated", null, _ordinal, Absolute(_consumedInBuffer));
            }

            return progressed;
        }

        private void Handle(ref Utf8JsonReader reader)
        {
            if (_phase == Phase.Done)
            {
                throw Refuse(DataContractViolation.DataInvalid, "trailing-content", null, _ordinal, Absolute(reader.TokenStartIndex));
            }

            if (_phase == Phase.Root)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw Refuse(DataContractViolation.DataInvalid, "profile-violation", null, 0, Absolute(reader.TokenStartIndex));
                }

                _phase = Phase.Properties;
                return;
            }

            if (_capturing)
            {
                Capture(ref reader);
                return;
            }

            switch (_phase)
            {
                case Phase.Properties:
                    Property(ref reader);
                    return;

                case Phase.Version:
                    Version(ref reader);
                    return;

                case Phase.Header:
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        throw Refuse(DataContractViolation.DataInvalid, "type-mismatch", null, 0, Absolute(reader.TokenStartIndex));
                    }

                    BeginCapture(header: true, ref reader);
                    return;

                case Phase.Items:
                    Item(ref reader);
                    return;

                default:
                    throw Refuse(DataContractViolation.DataInvalid, "profile-violation", null, _ordinal, Absolute(reader.TokenStartIndex));
            }
        }

        private void Property(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (!_versionSeen || !_headerSeen || !_itemsSeen)
                {
                    throw Refuse(DataContractViolation.DataInvalid, "profile-violation", null, _ordinal, Absolute(reader.TokenStartIndex));
                }

                _phase = Phase.Done;
                return;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw Refuse(DataContractViolation.DataInvalid, "malformed", null, _ordinal, Absolute(reader.TokenStartIndex));
            }

            var name = reader.ValueSpan;
            if (name.SequenceEqual("contractVersion"u8))
            {
                if (_versionSeen)
                {
                    throw Duplicate(reader);
                }

                if (_headerSeen || _itemsSeen)
                {
                    throw Refuse(DataContractViolation.DataInvalid, "profile-violation", null, 0, Absolute(reader.TokenStartIndex));
                }

                _phase = Phase.Version;
                return;
            }

            if (name.SequenceEqual("header"u8))
            {
                if (_headerSeen)
                {
                    throw Duplicate(reader);
                }

                // An items already opened the record array: the header, whenever it arrives,
                // is the order fault the profile names, and the refusal never depends on
                // whether it arrives at all.
                if (_itemsSeen)
                {
                    throw Refuse(DataContractViolation.DataInvalid, "header-not-first", null, 0, Absolute(reader.TokenStartIndex));
                }

                if (!_versionSeen)
                {
                    throw Refuse(DataContractViolation.DataInvalid, "profile-violation", null, 0, Absolute(reader.TokenStartIndex));
                }

                _phase = Phase.Header;
                return;
            }

            if (name.SequenceEqual("items"u8))
            {
                if (_itemsSeen)
                {
                    throw Duplicate(reader);
                }

                // The records come after the header and never before it: a pass that meets
                // this name without a header refuses at the name, before the array is entered,
                // and cannot read on to learn whether a header would have followed.
                if (!_headerSeen)
                {
                    throw Refuse(DataContractViolation.DataInvalid, "header-not-first", null, 0, Absolute(reader.TokenStartIndex));
                }

                _phase = Phase.Items;
                return;
            }

            throw Refuse(DataContractViolation.DataInvalid, "profile-violation", null, 0, Absolute(reader.TokenStartIndex));
        }

        private void Version(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.String || !reader.ValueSpan.SequenceEqual(ContractVersionUtf8))
            {
                throw Refuse(DataContractViolation.DataInvalid, "contract-version", null, 0, Absolute(reader.TokenStartIndex));
            }

            _versionSeen = true;
            _phase = Phase.Properties;
        }

        private void Item(ref Utf8JsonReader reader)
        {
            // The records array has not started yet: the only value the profile admits here
            // is an array, and anything else is refused before an element is captured, so no
            // object is ever delivered as if it were a record.
            if (!_itemsStarted)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw Refuse(DataContractViolation.DataInvalid, "type-mismatch", null, 0, Absolute(reader.TokenStartIndex));
                }

                _itemsStarted = true;
                return;
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                _itemsStarted = false;
                _itemsSeen = true;
                _phase = Phase.Properties;
                return;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw Refuse(DataContractViolation.DataInvalid, "profile-violation", null, _ordinal + 1, Absolute(reader.TokenStartIndex));
            }

            BeginCapture(header: false, ref reader);
        }

        private static ReadOnlySpan<byte> ContractVersionUtf8 => "1.0"u8;

        private void BeginCapture(bool header, ref Utf8JsonReader reader)
        {
            _capturing = true;
            _capturingHeader = header;
            _captureDepth = reader.CurrentDepth;
            _captureStart = Absolute(reader.TokenStartIndex);
            _previousValueEnd = false;
            _captured.ResetWrittenCount();
            Append('{');

            // The names of the value being captured are its own: the index starts empty and
            // the objects of the previous value give their frames back.
            _namesLength = 0;
            _frameTop = 0;
            PushFrame(_captureDepth);
        }

        private void Capture(ref Utf8JsonReader reader)
        {
            if (reader.CurrentDepth > DataContractLimits.Depth)
            {
                throw Refuse(DataContractViolation.QuotaExceeded, null, "depth", OrdinalOfCapture, Absolute(reader.TokenStartIndex));
            }

            // Between two members or two elements of a captured value the separator is
            // written here, so the captured text is a document of its own.
            if (_previousValueEnd && StartsValue(reader.TokenType))
            {
                Append(',');
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    Append('{');
                    _previousValueEnd = false;
                    PushFrame(reader.CurrentDepth);
                    break;

                case JsonTokenType.EndObject:
                    Append('}');
                    _previousValueEnd = true;
                    if (_frameTop > 0 && _frames[_frameTop - 1].Depth == reader.CurrentDepth)
                    {
                        _frameTop--;
                    }

                    break;

                case JsonTokenType.StartArray:
                    Append('[');
                    _previousValueEnd = false;
                    break;

                case JsonTokenType.EndArray:
                    Append(']');
                    _previousValueEnd = true;
                    break;

                case JsonTokenType.PropertyName:
                    // Before the name is written and before the limit of this token is
                    // checked: a repeated member is earlier in reading order than a limit
                    // the same token would cross, and nothing of the value is kept.
                    CheckDuplicate(ref reader);
                    AppendQuoted(reader.ValueSpan);
                    Append(':');
                    _previousValueEnd = false;
                    break;

                default:
                    AppendValue(ref reader);
                    _previousValueEnd = true;
                    break;
            }

            var limit = _capturingHeader ? DataContractLimits.HeaderBytes : DataContractLimits.RecordBytes;
            if (_captured.WrittenCount > limit)
            {
                throw _capturingHeader
                    ? Refuse(DataContractViolation.QuotaExceeded, null, "header", 0, _captureStart)
                    : Refuse(DataContractViolation.RecordTooLarge, null, "record", OrdinalOfCapture, _captureStart);
            }

            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == _captureDepth)
            {
                Complete(ref reader);
            }
        }

        private int OrdinalOfCapture => _capturingHeader ? 0 : _ordinal + 1;

        private void Complete(ref Utf8JsonReader reader)
        {
            var end = Absolute(reader.BytesConsumed);
            var value = new RawRecord(OrdinalOfCapture, _captured.WrittenSpan.ToArray(), end - _captureStart, _captureStart);
            _capturing = false;

            if (_capturingHeader)
            {
                _headerSeen = true;
                _phase = Phase.Properties;
            }
            else
            {
                _ordinal++;
                _phase = Phase.Items;
            }

            _staged = value;
        }

        private static bool StartsValue(JsonTokenType type) => type is JsonTokenType.StartObject
            or JsonTokenType.StartArray
            or JsonTokenType.PropertyName
            or JsonTokenType.String
            or JsonTokenType.Number
            or JsonTokenType.True
            or JsonTokenType.False
            or JsonTokenType.Null;

        private void Append(char value) => Append((byte)value);

        private void Append(byte value)
        {
            var span = _captured.GetSpan(1);
            span[0] = value;
            _captured.Advance(1);
        }

        private void AppendQuoted(ReadOnlySpan<byte> raw)
        {
            Append('"');
            var span = _captured.GetSpan(raw.Length);
            raw.CopyTo(span);
            _captured.Advance(raw.Length);
            Append('"');
        }

        private void AppendValue(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    AppendQuoted(reader.ValueSpan);
                    break;

                case JsonTokenType.Number:
                    AppendRaw(reader.ValueSpan);
                    break;

                case JsonTokenType.True:
                    AppendRaw("true"u8);
                    break;

                case JsonTokenType.False:
                    AppendRaw("false"u8);
                    break;

                case JsonTokenType.Null:
                    AppendRaw("null"u8);
                    break;

                default:
                    throw Refuse(DataContractViolation.DataInvalid, "malformed", null, OrdinalOfCapture, Absolute(reader.TokenStartIndex));
            }
        }

        private void AppendRaw(ReadOnlySpan<byte> raw)
        {
            raw.CopyTo(_captured.GetSpan(raw.Length));
            _captured.Advance(raw.Length);
        }

        // The objects of the value being captured, one frame per open object: the depth it
        // opened at and the names it has read so far, as ranges of the pass's name buffer
        // and a hash lookup over them.
        private void PushFrame(int depth)
        {
            if (_frameTop == _frames.Length)
            {
                return;
            }

            // A frame the previous value used keeps its buffers and gives its names back.
            if (_frames[_frameTop].Count > 0)
            {
                Array.Clear(_frames[_frameTop].Slots!);
                _frames[_frameTop].Count = 0;
            }

            _frames[_frameTop].Depth = depth;
            _frameTop++;
        }

        private void CheckDuplicate(ref Utf8JsonReader reader)
        {
            if (_frameTop == 0 || _frames[_frameTop - 1].Depth != reader.CurrentDepth - 1)
            {
                return;
            }

            // Unescaping yields no more bytes than the token's own text, so the destination is
            // sized by that text and the name is copied exactly once, as the value it has.
            var length = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
            var start = ReserveName(length);
            var destination = _names.AsSpan(start, length);
            int written;
            try
            {
                written = reader.CopyString(destination);
            }
            catch (InvalidOperationException)
            {
                // An escape that is not a Unicode scalar sequence leaves the name read as its
                // own text, exactly as the value keeps it, so two equal texts are one member.
                CopyRaw(ref reader, destination);
                written = length;
            }

            var frame = _frameTop - 1;
            var hash = Hash(_names.AsSpan(start, written));
            if (Contains(frame, hash, start, written))
            {
                // Nothing of this value is kept: the bytes of the name just read are left
                // where they are and the pass stops at the name it refuses.
                throw Refuse(
                    DataContractViolation.DataInvalid,
                    "duplicate-property",
                    null,
                    OrdinalOfCapture,
                    Absolute(reader.TokenStartIndex));
            }

            Index(frame, hash, start, written);
            _namesLength = start + written;
        }

        private int ReserveName(int length)
        {
            var start = _namesLength;
            if (start + length > _names.Length)
            {
                var grown = new byte[Math.Max(_names.Length * 2, start + length)];
                Buffer.BlockCopy(_names, 0, grown, 0, start);
                _names = grown;
            }

            return start;
        }

        private bool Contains(int frame, ulong hash, int offset, int length)
        {
            var slots = _frames[frame].Slots;
            if (slots is null || slots.Length == 0)
            {
                return false;
            }

            var ranges = _frames[frame].Ranges!;
            var mask = _frames[frame].Mask;
            var candidate = _names.AsSpan(offset, length);
            var slot = (int)(hash & (ulong)mask);
            while (slots[slot] != 0)
            {
                var entry = slots[slot] - 1;
                if (_names.AsSpan(ranges[entry * 2], ranges[entry * 2 + 1]).SequenceEqual(candidate))
                {
                    return true;
                }

                slot = (slot + 1) & mask;
            }

            return false;
        }

        private void Index(int frame, ulong hash, int offset, int length)
        {
            ref var names = ref _frames[frame];
            var needed = (names.Count + 1) * 2;
            if (names.Ranges is null || needed > names.Ranges.Length)
            {
                Array.Resize(ref names.Ranges, names.Ranges is null ? 8 : names.Ranges.Length * 2);
            }

            // The lookup is kept at most half full, so a name is found in constant time in
            // the number of names the object holds, whatever that number is.
            if (names.Slots is null || needed > names.Slots.Length)
            {
                Rehash(ref names);
            }

            var ranges = names.Ranges!;
            ranges[names.Count * 2] = offset;
            ranges[names.Count * 2 + 1] = length;
            var slot = (int)(hash & (ulong)names.Mask);
            var slots = names.Slots!;
            while (slots[slot] != 0)
            {
                slot = (slot + 1) & names.Mask;
            }

            slots[slot] = names.Count + 1;
            names.Count++;
        }

        private void Rehash(ref NameFrame frame)
        {
            var slots = new int[frame.Slots is null ? 8 : frame.Slots.Length * 2];
            var mask = slots.Length - 1;
            for (var entry = 0; entry < frame.Count; entry++)
            {
                var hash = Hash(_names.AsSpan(frame.Ranges![entry * 2], frame.Ranges[entry * 2 + 1]));
                var slot = (int)(hash & (ulong)mask);
                while (slots[slot] != 0)
                {
                    slot = (slot + 1) & mask;
                }

                slots[slot] = entry + 1;
            }

            frame.Slots = slots;
            frame.Mask = mask;
        }

        private static ulong Hash(ReadOnlySpan<byte> name)
        {
            var hash = 14695981039346656037UL;
            foreach (var value in name)
            {
                hash ^= value;
                hash *= 1099511628211UL;
            }

            return hash;
        }

        private static void CopyRaw(ref Utf8JsonReader reader, Span<byte> destination)
        {
            if (reader.HasValueSequence)
            {
                reader.ValueSequence.CopyTo(destination);
            }
            else
            {
                reader.ValueSpan.CopyTo(destination);
            }
        }

        private void Compact()
        {
            if (_consumedInBuffer == 0)
            {
                return;
            }

            var remaining = _filled - _consumedInBuffer;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_buffer, _consumedInBuffer, _buffer, 0, remaining);
            }

            _consumedBeforeBuffer += _consumedInBuffer;
            _filled = remaining;
            _consumedInBuffer = 0;
        }

        private long Absolute(long indexInBuffer) => _baseOffset + indexInBuffer;

        private DataContractViolation Duplicate(Utf8JsonReader reader) =>
            Refuse(DataContractViolation.DataInvalid, "duplicate-property", null, _ordinal, Absolute(reader.TokenStartIndex));

        private static DataContractViolation Refuse(string code, string? reason, string? limit, int ordinal, long offset) =>
            new(code, reason, limit, ordinal, offset);
    }
}
