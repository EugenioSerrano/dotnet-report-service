using System.Runtime.CompilerServices;
using System.Text.Json;

namespace StreamingDataSpike.Parsing;

/// <summary>
/// Reads a <c>header-items-v1</c> envelope incrementally from a stream: the
/// header first, then one record at a time, never the whole document.
/// </summary>
/// <remarks>
/// <para>
/// The reader is fed successive buffers (<c>isFinalBlock: false</c>) and its
/// state is kept between them. A document that ends early therefore never
/// reaches the JSON reader as complete: it is always reported as
/// <c>reason=truncated</c>, never as a generic syntax error.
/// </para>
/// <para>
/// The header and each record are captured as contiguous bytes: when one does
/// not fit in the buffer, it is moved to the start of the buffer, the buffer
/// doubles (up to <see cref="EnvelopeLimits.BufferCap"/>) and the element is
/// read again from its saved state. The captured bytes are then materialised
/// by a second reader.
/// </para>
/// </remarks>
public sealed class EnvelopeReader
{
    private readonly CountingStream _input;
    private readonly EnvelopeLimits _limits;
    private readonly JsonReaderOptions _options;
    private readonly int _cap;

    private byte[] _buffer;
    private int _start;
    private int _end;
    private long _origin;
    private bool _endOfStream;
    private JsonReaderState _state;
    private Stage _stage;
    private Pending _pending;
    private int _pendingElementStart;
    private int _pendingTokenStart;
    private EnvelopeHeader? _header;
    private EnvelopeRecord? _record;

    /// <summary>Creates a reader over <paramref name="input"/> (plain or already decompressed).</summary>
    public EnvelopeReader(Stream input, EnvelopeLimits limits)
    {
        _input = new CountingStream(input, limits.InputLimit);
        _limits = limits;
        _options = new JsonReaderOptions
        {
            MaxDepth = limits.DepthLimit + 1,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };
        _cap = limits.BufferCap;
        _buffer = new byte[Math.Clamp(limits.InitialBuffer, 16, _cap)];
        MaxBufferBytes = _buffer.Length;
        _state = new JsonReaderState(_options);
    }

    private enum Stage
    {
        RootStart,
        ContractVersionName,
        ContractVersionValue,
        HeaderName,
        HeaderValue,
        ItemsName,
        ItemsValue,
        Items,
        RootEnd,
        Trailing,
        Done,
    }

    private enum Pending
    {
        Navigation,
        Header,
        Item,
    }

    private enum StepResult
    {
        NeedMore,
        Header,
        Item,
        End,
    }

    /// <summary>Records completely read so far.</summary>
    public long RecordsRead { get; private set; }

    /// <summary>Largest read buffer used, in bytes.</summary>
    public int MaxBufferBytes { get; private set; }

    /// <summary>Largest record seen, in bytes.</summary>
    public long MaxRecordBytes { get; private set; }

    /// <summary>Uncompressed bytes read from the input so far.</summary>
    public long InputBytes => _input.BytesRead;

    /// <summary>Reads the envelope up to the end of the header.</summary>
    public async ValueTask<EnvelopeHeader> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (Step())
            {
                case StepResult.Header:
                    return _header!;
                case StepResult.NeedMore:
                    await RefillAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("The header was not found where expected.");
            }
        }
    }

    /// <summary>
    /// Reads the records one at a time. The next record is read only when the
    /// consumer asks for it; after the last one the end of the envelope and
    /// the absence of trailing content are checked.
    /// </summary>
    public async IAsyncEnumerable<EnvelopeRecord> ReadItemsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_header is null)
        {
            throw new InvalidOperationException("Read the header first.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (Step())
            {
                case StepResult.Item:
                    var record = _record!;
                    _record = null;
                    yield return record;
                    break;
                case StepResult.NeedMore:
                    await RefillAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case StepResult.End:
                    yield break;
                default:
                    throw new InvalidOperationException("Unexpected reader step.");
            }
        }
    }

    private long CurrentOrdinal => _stage is Stage.Items ? RecordsRead + 1 : 0;

    private StepResult Step()
    {
        if (_stage == Stage.Done)
        {
            return StepResult.End;
        }

        if (_stage == Stage.Trailing)
        {
            return ScanTrailing();
        }

        var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _end - _start), isFinalBlock: false, _state);
        var ordinal = CurrentOrdinal;
        try
        {
            while (true)
            {
                var markConsumed = (int)reader.BytesConsumed;
                var markState = reader.CurrentState;
                if (!reader.Read())
                {
                    _pending = Pending.Navigation;
                    Commit((int)reader.BytesConsumed, reader.CurrentState);
                    return StepResult.NeedMore;
                }

                ordinal = CurrentOrdinal;
                CheckToken(ref reader, ordinal);
                switch (_stage)
                {
                    case Stage.RootStart:
                        Expect(ref reader, JsonTokenType.StartObject, "type-mismatch", 0);
                        _stage = Stage.ContractVersionName;
                        break;

                    case Stage.ContractVersionName:
                        if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("contractVersion"u8))
                        {
                            _stage = Stage.ContractVersionValue;
                        }
                        else if (reader.TokenType == JsonTokenType.PropertyName && !IsEnvelopeProperty(ref reader))
                        {
                            throw Invalid(ref reader, "unexpected-property", 0);
                        }
                        else
                        {
                            throw Invalid(ref reader, "contract-version", 0);
                        }

                        break;

                    case Stage.ContractVersionValue:
                        if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals("1.0"u8))
                        {
                            throw Invalid(ref reader, "contract-version", 0);
                        }

                        _stage = Stage.HeaderName;
                        break;

                    case Stage.HeaderName:
                        if (reader.TokenType == JsonTokenType.EndObject)
                        {
                            throw Invalid(ref reader, "missing-items", 0);
                        }

                        if (reader.ValueTextEquals("header"u8))
                        {
                            _stage = Stage.HeaderValue;
                        }
                        else
                        {
                            throw Invalid(ref reader, PropertyReason(ref reader, "header-not-first"), 0);
                        }

                        break;

                    case Stage.HeaderValue:
                        Expect(ref reader, JsonTokenType.StartObject, "type-mismatch", 0);
                        if (!TryCapture(ref reader, markConsumed, markState, Pending.Header, 0, out var headerStart, out var headerEnd))
                        {
                            return StepResult.NeedMore;
                        }

                        var headerBytes = _buffer.AsSpan(_start + headerStart, headerEnd - headerStart);
                        var header = JsonValueModel.ReadObject(headerBytes, _options, 0, _origin + _start + headerStart);
                        _header = new EnvelopeHeader(header, headerEnd - headerStart);
                        _stage = Stage.ItemsName;
                        Commit((int)reader.BytesConsumed, reader.CurrentState);
                        return StepResult.Header;

                    case Stage.ItemsName:
                        if (reader.TokenType == JsonTokenType.EndObject)
                        {
                            throw Invalid(ref reader, "missing-items", 0);
                        }

                        if (reader.ValueTextEquals("items"u8))
                        {
                            _stage = Stage.ItemsValue;
                        }
                        else
                        {
                            throw Invalid(ref reader, PropertyReason(ref reader, "unexpected-property"), 0);
                        }

                        break;

                    case Stage.ItemsValue:
                        Expect(ref reader, JsonTokenType.StartArray, "type-mismatch", 0);
                        _stage = Stage.Items;
                        break;

                    case Stage.Items:
                        if (reader.TokenType == JsonTokenType.EndArray)
                        {
                            _stage = Stage.RootEnd;
                            break;
                        }

                        Expect(ref reader, JsonTokenType.StartObject, "type-mismatch", ordinal);
                        if (!TryCapture(ref reader, markConsumed, markState, Pending.Item, ordinal, out var itemStart, out var itemEnd))
                        {
                            return StepResult.NeedMore;
                        }

                        var itemLength = itemEnd - itemStart;
                        var itemOffset = _origin + _start + itemStart;
                        var item = JsonValueModel.ReadObject(_buffer.AsSpan(_start + itemStart, itemLength), _options, ordinal, itemOffset);
                        RecordsRead = ordinal;
                        MaxRecordBytes = Math.Max(MaxRecordBytes, itemLength);
                        _record = new EnvelopeRecord(ordinal, item, itemLength, itemOffset);
                        Commit((int)reader.BytesConsumed, reader.CurrentState);
                        return StepResult.Item;

                    case Stage.RootEnd:
                        if (reader.TokenType != JsonTokenType.EndObject)
                        {
                            throw Invalid(ref reader, PropertyReason(ref reader, "unexpected-property"), 0);
                        }

                        _stage = Stage.Trailing;
                        Commit((int)reader.BytesConsumed, reader.CurrentState);
                        return ScanTrailing();

                    default:
                        throw new InvalidOperationException("Unexpected reader stage.");
                }
            }
        }
        catch (JsonException)
        {
            throw EnvelopeException.Invalid("malformed", ordinal, _origin + _start + reader.BytesConsumed);
        }
    }

    /// <summary>
    /// Reads the element that starts at the current token up to its matching
    /// end. When the buffer runs out first, the reader is rolled back to the
    /// position before the element so that it is read again after a refill.
    /// </summary>
    private bool TryCapture(ref Utf8JsonReader reader, int markConsumed, JsonReaderState markState, Pending kind, long ordinal, out int start, out int end)
    {
        start = (int)reader.TokenStartIndex;
        var depth = reader.CurrentDepth;
        while (true)
        {
            if (!reader.Read())
            {
                _pending = kind;
                _pendingElementStart = _start + start;
                _pendingTokenStart = _start + (int)reader.BytesConsumed;
                Commit(markConsumed, markState);
                end = 0;
                return false;
            }

            CheckToken(ref reader, ordinal);
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                end = (int)reader.BytesConsumed;
                CheckElementLength(kind, end - start, ordinal, _origin + _start + start);
                return true;
            }
        }
    }

    private void CheckElementLength(Pending kind, long length, long ordinal, long offset)
    {
        if (kind == Pending.Header && length > _limits.HeaderLimit)
        {
            throw EnvelopeException.ForLimit(ErrorCodes.QuotaExceeded, "header", 0, offset);
        }

        if (kind == Pending.Item && length > _limits.RecordLimit)
        {
            throw EnvelopeException.ForLimit(ErrorCodes.RecordTooLarge, "record", ordinal, offset);
        }
    }

    private void CheckToken(ref Utf8JsonReader reader, long ordinal)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth + 1 > _limits.DepthLimit)
        {
            throw EnvelopeException.ForLimit(ErrorCodes.QuotaExceeded, "depth", ordinal, _origin + _start + reader.TokenStartIndex);
        }

        if (reader.ValueSpan.Length > _limits.TokenLimit)
        {
            throw EnvelopeException.ForLimit(ErrorCodes.QuotaExceeded, "token", ordinal, _origin + _start + reader.TokenStartIndex);
        }
    }

    private void Expect(ref Utf8JsonReader reader, JsonTokenType type, string reason, long ordinal)
    {
        if (reader.TokenType != type)
        {
            throw Invalid(ref reader, reason, ordinal);
        }
    }

    private EnvelopeException Invalid(ref Utf8JsonReader reader, string reason, long ordinal) =>
        EnvelopeException.Invalid(reason, ordinal, _origin + _start + reader.TokenStartIndex);

    private static bool IsEnvelopeProperty(ref Utf8JsonReader reader) =>
        reader.ValueTextEquals("contractVersion"u8) || reader.ValueTextEquals("header"u8) || reader.ValueTextEquals("items"u8);

    /// <summary>Reason for a root property found where another one was expected.</summary>
    private string PropertyReason(ref Utf8JsonReader reader, string whenEnvelopeProperty)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
        {
            return "type-mismatch";
        }

        if (!IsEnvelopeProperty(ref reader))
        {
            return "unexpected-property";
        }

        var seenHeader = _stage is Stage.ItemsName or Stage.RootEnd;
        var seenItems = _stage is Stage.RootEnd;
        if (reader.ValueTextEquals("contractVersion"u8)
            || (seenHeader && reader.ValueTextEquals("header"u8))
            || (seenItems && reader.ValueTextEquals("items"u8)))
        {
            return "duplicate-property";
        }

        return whenEnvelopeProperty;
    }

    private StepResult ScanTrailing()
    {
        for (var i = _start; i < _end; i++)
        {
            var b = _buffer[i];
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
            {
                throw EnvelopeException.Invalid("trailing-content", 0, _origin + i);
            }
        }

        _start = _end;
        if (_endOfStream)
        {
            _stage = Stage.Done;
            return StepResult.End;
        }

        _pending = Pending.Navigation;
        return StepResult.NeedMore;
    }

    private void Commit(int consumed, JsonReaderState state)
    {
        _start += consumed;
        _state = state;
    }

    /// <summary>
    /// Makes room and reads more input: enforces the element limits before any
    /// growth, moves the unconsumed bytes to the start of the buffer, doubles
    /// the buffer when nothing could be consumed, then fills it.
    /// </summary>
    private async ValueTask RefillAsync(CancellationToken cancellationToken)
    {
        var ordinal = _pending == Pending.Header ? 0 : CurrentOrdinal;
        if (_endOfStream)
        {
            throw EnvelopeException.Invalid("truncated", ordinal, _origin + _end);
        }

        if (_pending is Pending.Header or Pending.Item)
        {
            CheckElementLength(_pending, _end - _pendingElementStart, ordinal, _origin + _pendingElementStart);
            if (_end - _pendingTokenStart > _limits.TokenLimit)
            {
                throw EnvelopeException.ForLimit(ErrorCodes.QuotaExceeded, "token", ordinal, _origin + _pendingTokenStart);
            }
        }

        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _origin += _start;
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= _cap)
            {
                throw _pending switch
                {
                    Pending.Header => EnvelopeException.ForLimit(ErrorCodes.QuotaExceeded, "header", 0, _origin),
                    Pending.Item => EnvelopeException.ForLimit(ErrorCodes.RecordTooLarge, "record", ordinal, _origin),
                    _ => EnvelopeException.ForLimit(ErrorCodes.QuotaExceeded, "token", ordinal, _origin),
                };
            }

            var larger = new byte[Math.Min(_buffer.Length * 2, _cap)];
            Buffer.BlockCopy(_buffer, 0, larger, 0, _end);
            _buffer = larger;
            MaxBufferBytes = Math.Max(MaxBufferBytes, larger.Length);
        }

        try
        {
            while (_end < _buffer.Length)
            {
                var read = await _input.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    _endOfStream = true;
                    break;
                }

                _end += read;
            }
        }
        catch (InputLimitExceededException exception)
        {
            throw EnvelopeException.ForLimit(ErrorCodes.QuotaExceeded, "input-bytes", ordinal, exception.BytesRead);
        }
    }
}
