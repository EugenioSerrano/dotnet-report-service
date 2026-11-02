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
                        throw Refuse(DataContractViolation.DataInvalid, "profile-violation", null, 0, Absolute(reader.TokenStartIndex));
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

                if (!_versionSeen || _itemsSeen)
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

                if (!_headerSeen)
                {
                    throw Refuse(DataContractViolation.DataInvalid, "profile-violation", null, 0, Absolute(reader.TokenStartIndex));
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
            if (reader.TokenType == JsonTokenType.StartArray && !_itemsStarted)
            {
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
                    break;

                case JsonTokenType.EndObject:
                    Append('}');
                    _previousValueEnd = true;
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
