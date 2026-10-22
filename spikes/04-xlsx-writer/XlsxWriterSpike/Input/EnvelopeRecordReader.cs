using System.Text;
using System.Text.Json;
using XlsxWriterSpike.Typing;

namespace XlsxWriterSpike.Input;

/// <summary>
/// Streams the records of a <c>header-items-v1</c> JSON file one at a time with
/// <see cref="Utf8JsonReader"/>: the document is never parsed as a whole and no
/// list of records is kept. The header (which precedes <c>items</c>) is read on
/// open to obtain <c>totals.recordCount</c>.
/// </summary>
internal sealed class EnvelopeRecordReader : IDisposable
{
    private const int InitialBufferSize = 64 * 1024;
    private const int MaxBufferSize = 2 * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly ColumnProfile _profile;
    private readonly byte[][] _columnNames;
    private byte[] _buffer = new byte[InitialBufferSize];
    private int _start;
    private int _end;
    private bool _finalBlock;
    private JsonReaderState _state;

    private string? _rootProperty;
    private string? _headerProperty;
    private bool _expectRecordCount;
    private bool _inItems;
    private bool _itemsDone;
    private int _currentColumn = -1;
    private bool _inRecord;
    private long _recordNumber;

    private EnvelopeRecordReader(string path, ColumnProfile profile)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
        _profile = profile;
        _columnNames = [.. profile.Columns.Select(c => Encoding.UTF8.GetBytes(c.Name))];
        _state = new JsonReaderState(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
    }

    /// <summary><c>header.totals.recordCount</c>, or <c>null</c> when the header has none.</summary>
    public long? RecordCount { get; private set; }

    /// <summary>
    /// Opens the file, reads the header up to the start of <c>items</c> and
    /// discards the first <paramref name="skip"/> records.
    /// </summary>
    public static EnvelopeRecordReader Open(string path, ColumnProfile profile, long skip)
    {
        var reader = new EnvelopeRecordReader(path, profile);
        try
        {
            reader.Pump(stopAtRecord: false, null);
            if (!reader._inItems)
            {
                throw new SpikeException($"'{Path.GetFileName(path)}' has no 'items' array.");
            }

            var scratch = new RawRecord(profile.Columns.Count);
            for (long i = 0; i < skip && reader.TryReadNext(scratch); i++)
            {
            }

            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>Reads the next record into <paramref name="record"/>; <c>false</c> at the end of <c>items</c>.</summary>
    public bool TryReadNext(RawRecord record)
    {
        if (_itemsDone)
        {
            return false;
        }

        record.Reset();
        return Pump(stopAtRecord: true, record);
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();

    /// <summary>
    /// Reads tokens until a record is complete (<paramref name="stopAtRecord"/>),
    /// or until the <c>items</c> array starts (header phase). Parser state is kept
    /// in fields so that a refill can happen between any two tokens.
    /// </summary>
    private bool Pump(bool stopAtRecord, RawRecord? record)
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _end - _start), _finalBlock, _state);
            var stop = false;
            var result = false;
            while (!stop && reader.Read())
            {
                if (!_inItems)
                {
                    stop = HeaderToken(ref reader);
                }
                else if (stopAtRecord)
                {
                    stop = ItemToken(ref reader, record!, out result);
                }
            }

            _start += (int)reader.BytesConsumed;
            _state = reader.CurrentState;
            if (stop)
            {
                return result;
            }

            if (_finalBlock)
            {
                throw new SpikeException("Unexpected end of the JSON file.");
            }

            Refill();
        }
    }

    private bool HeaderToken(ref Utf8JsonReader reader)
    {
        var depth = reader.CurrentDepth;
        switch (reader.TokenType)
        {
            case JsonTokenType.PropertyName when depth == 1:
                _rootProperty = reader.GetString();
                break;
            case JsonTokenType.PropertyName when depth == 2:
                _headerProperty = reader.GetString();
                break;
            case JsonTokenType.PropertyName when depth == 3:
                _expectRecordCount = _rootProperty == "header" && _headerProperty == "totals" && reader.ValueTextEquals("recordCount"u8);
                break;
            case JsonTokenType.Number when _expectRecordCount:
                RecordCount = reader.GetInt64();
                _expectRecordCount = false;
                break;
            case JsonTokenType.StartArray when depth == 1 && _rootProperty == "items":
                _inItems = true;
                return true;
            default:
                _expectRecordCount = false;
                break;
        }

        return false;
    }

    private bool ItemToken(ref Utf8JsonReader reader, RawRecord record, out bool recordRead)
    {
        recordRead = false;
        var depth = reader.CurrentDepth;
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject when depth == 2:
                _inRecord = true;
                _recordNumber++;
                record.RecordNumber = _recordNumber;
                return false;
            case JsonTokenType.EndObject when depth == 2:
                _inRecord = false;
                for (var i = 0; i < record.Values.Length; i++)
                {
                    if (record.Values[i].Token == JsonTokenType.None)
                    {
                        throw new SpikeException(
                            $"Record {_recordNumber} has no property '{_profile.Columns[i].Name}' declared by the column profile.");
                    }
                }

                recordRead = true;
                return true;
            case JsonTokenType.EndArray when depth == 1:
                _itemsDone = true;
                return true;
            case JsonTokenType.PropertyName when depth == 3 && _inRecord:
                _currentColumn = -1;
                for (var i = 0; i < _columnNames.Length; i++)
                {
                    if (reader.ValueTextEquals(_columnNames[i]))
                    {
                        _currentColumn = i;
                        break;
                    }
                }

                if (_currentColumn < 0)
                {
                    throw new SpikeException(
                        $"Record {_recordNumber} has property '{reader.GetString()}' that the column profile does not declare.");
                }

                return false;
            case JsonTokenType.String when depth == 3:
                Set(record, JsonTokenType.String, reader.GetString());
                return false;
            case JsonTokenType.Number when depth == 3:
                Set(record, JsonTokenType.Number, Encoding.UTF8.GetString(reader.ValueSpan));
                return false;
            case JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null when depth == 3:
                Set(record, reader.TokenType, null);
                return false;
            default:
                throw new SpikeException(
                    $"Record {_recordNumber}: unexpected JSON token {reader.TokenType} (nested values are not supported).");
        }
    }

    private void Set(RawRecord record, JsonTokenType token, string? text)
    {
        ref var value = ref record.Values[_currentColumn];
        value.Token = token;
        value.Text = text;
    }

    private void Refill()
    {
        var remaining = _end - _start;
        if (remaining == _buffer.Length)
        {
            if (_buffer.Length >= MaxBufferSize)
            {
                throw new SpikeException($"A JSON token is larger than {MaxBufferSize} bytes.");
            }

            var larger = new byte[_buffer.Length * 2];
            Buffer.BlockCopy(_buffer, _start, larger, 0, remaining);
            _buffer = larger;
        }
        else if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, remaining);
        }

        _start = 0;
        _end = remaining;
        var read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read == 0)
        {
            _finalBlock = true;
        }

        _end += read;
    }
}
