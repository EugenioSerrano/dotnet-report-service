using System.Globalization;

namespace ReportService.Data.Partitioning;

/// <summary>
/// A record's group key: the value of the field the template manifest names in
/// <c>partitioning.groupKey</c>, a string or an integer, compared by type and exact
/// value. A string key and an integer key of the same text are
/// different groups.
/// </summary>
public readonly record struct GroupKey
{
    private readonly string? _text;
    private readonly long _integer;
    private readonly bool _isInteger;

    private GroupKey(string? text, long integer, bool isInteger)
    {
        _text = text;
        _integer = integer;
        _isInteger = isInteger;
    }

    /// <summary>A string group key.</summary>
    /// <param name="value">The key's text.</param>
    public static GroupKey Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GroupKey(value, 0, isInteger: false);
    }

    /// <summary>An integer group key.</summary>
    /// <param name="value">The key's value.</param>
    public static GroupKey Integer(long value) => new(null, value, isInteger: true);

    /// <summary>Whether this key and <paramref name="other"/> are the same group: same type and exact value.</summary>
    /// <param name="other">The key to compare with.</param>
    public bool SameGroup(GroupKey other) =>
        _isInteger == other._isInteger
        && (_isInteger
            ? _integer == other._integer
            : string.Equals(_text, other._text, StringComparison.Ordinal));

    /// <summary>The key as the text the index records under <c>openingGroupKey</c>.</summary>
    public string ToText() => _isInteger ? _integer.ToString(CultureInfo.InvariantCulture) : _text ?? string.Empty;
}

/// <summary>
/// Tracks the contiguous group runs of a record stream: a group is a run of records with
/// an equal group key, and a boundary is where the key changes. A key that reappears
/// after a different one starts a new group — contiguity is a producer obligation, so the
/// tracker remembers only the current key, never the history.
/// </summary>
public sealed class GroupBoundaryTracker
{
    private GroupKey _current;
    private bool _started;

    /// <summary>The key last observed.</summary>
    public GroupKey Current => _current;

    /// <summary>
    /// Observes the next record's key and answers whether it starts a new group. The first
    /// record always starts a group; an equal key continues the current group; a changed key
    /// starts a new one.
    /// </summary>
    /// <param name="key">The next record's group key.</param>
    /// <returns><see langword="true"/> when the key starts a new group.</returns>
    public bool Observe(GroupKey key)
    {
        if (!_started)
        {
            _started = true;
            _current = key;
            return true;
        }

        if (_current.SameGroup(key))
        {
            return false;
        }

        _current = key;
        return true;
    }
}
