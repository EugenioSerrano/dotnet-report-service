namespace ReportService.Storage;

/// <summary>
/// An object's key: segments joined by <c>/</c>, each matching
/// <c>[a-z0-9]([a-z0-9._-]{0,126}[a-z0-9])?</c>, none equal to <c>.</c>,
/// <c>..</c> or a reserved device name, at most <see cref="MaxSegments"/>
/// segments and <see cref="MaxLength"/> characters. Upper case is refused,
/// never lowered. A key is built only through <see cref="Create"/>,
/// <see cref="FromSegments"/> or <see cref="TryCreate"/>; every port method
/// takes a key, never a string, so caller text cannot reach a store
/// unvalidated. Equality and ordering are ordinal. A refusal reason never
/// repeats the refused text.
/// </summary>
public readonly struct ObjectKey : IEquatable<ObjectKey>, IComparable<ObjectKey>
{
    /// <summary>The longest segment, in characters.</summary>
    public const int MaxSegmentLength = 128;

    /// <summary>The most segments a key may have.</summary>
    public const int MaxSegments = 16;

    /// <summary>The longest key, in characters.</summary>
    public const int MaxLength = 512;

    /// <summary>The separator of segments.</summary>
    public const char Separator = '/';

    private static readonly string[] DeviceNames =
    [
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    ];

    private readonly string? _value;

    private ObjectKey(string value) => _value = value;

    /// <summary>True for the default value, which is not a key and is refused by every store.</summary>
    public bool IsEmpty => _value is null;

    /// <summary>The key's segments, in order.</summary>
    public IReadOnlyList<string> Segments => _value is null ? [] : _value.Split(Separator);

    /// <summary>Builds a key, throwing <see cref="InvalidObjectKeyException"/> naming the rule it breaks.</summary>
    public static ObjectKey Create(string value) =>
        TryCreate(value, out var key, out var reason) ? key : throw new InvalidObjectKeyException(reason);

    /// <summary>Builds a key from its segments, each checked on its own before the whole key is.</summary>
    public static ObjectKey FromSegments(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var segment in segments)
        {
            if (segment is null || segment.Contains(Separator, StringComparison.Ordinal))
            {
                throw new InvalidObjectKeyException("a segment is missing or contains '/'");
            }
        }

        return Create(string.Join(Separator, segments));
    }

    /// <summary>Builds a key, or returns false with the rule it breaks.</summary>
    public static bool TryCreate(string? value, out ObjectKey key, out string reason)
    {
        key = default;
        var problem = Check(value);
        if (problem is not null)
        {
            reason = problem;
            return false;
        }

        key = new ObjectKey(value!);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Checks a listing prefix: one or more segments under the same rules as a
    /// key (a prefix is the leading part of a key), with no wildcard.
    /// </summary>
    public static bool IsValidPrefix(string? prefix, out string reason)
    {
        reason = Check(prefix) ?? string.Empty;
        return reason.Length == 0;
    }

    /// <inheritdoc />
    public override string ToString() => _value ?? string.Empty;

    /// <inheritdoc />
    public bool Equals(ObjectKey other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ObjectKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    /// <inheritdoc />
    public int CompareTo(ObjectKey other) => string.CompareOrdinal(_value, other._value);

    /// <summary>Ordinal equality.</summary>
    public static bool operator ==(ObjectKey left, ObjectKey right) => left.Equals(right);

    /// <summary>Ordinal inequality.</summary>
    public static bool operator !=(ObjectKey left, ObjectKey right) => !left.Equals(right);

    /// <summary>Ordinal order.</summary>
    public static bool operator <(ObjectKey left, ObjectKey right) => left.CompareTo(right) < 0;

    /// <summary>Ordinal order.</summary>
    public static bool operator <=(ObjectKey left, ObjectKey right) => left.CompareTo(right) <= 0;

    /// <summary>Ordinal order.</summary>
    public static bool operator >(ObjectKey left, ObjectKey right) => left.CompareTo(right) > 0;

    /// <summary>Ordinal order.</summary>
    public static bool operator >=(ObjectKey left, ObjectKey right) => left.CompareTo(right) >= 0;

    // The checks run in a fixed order, so a refused value always gets the same reason.
    private static string? Check(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "the key is empty";
        }

        if (value.Length > MaxLength)
        {
            return "the key is longer than 512 characters";
        }

        foreach (var c in value)
        {
            if (c > '\u007f')
            {
                return "the key contains a character outside ASCII";
            }
        }

        if (value.Contains('\\', StringComparison.Ordinal))
        {
            return "the key contains a backslash";
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            return "the key contains ':'";
        }

        if (value[0] == Separator || value[^1] == Separator)
        {
            return "the key starts or ends with '/'";
        }

        var segments = value.Split(Separator);
        if (segments.Length > MaxSegments)
        {
            return "the key has more than 16 segments";
        }

        foreach (var segment in segments)
        {
            if (CheckSegment(segment) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? CheckSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return "the key has an empty segment";
        }

        if (segment is "." or "..")
        {
            return "a segment is '.' or '..'";
        }

        if (segment.Length > MaxSegmentLength)
        {
            return "a segment is longer than 128 characters";
        }

        foreach (var c in segment)
        {
            if (c is >= 'A' and <= 'Z')
            {
                return "a segment contains an upper-case letter";
            }
        }

        if (!IsAlphanumeric(segment[0]) || !IsAlphanumeric(segment[^1]))
        {
            return "a segment does not start and end with a lower-case letter or a digit";
        }

        foreach (var c in segment)
        {
            if (!IsAlphanumeric(c) && c is not ('.' or '_' or '-'))
            {
                return "a segment contains a character other than a-z, 0-9, '.', '_' or '-'";
            }
        }

        var stem = segment.Split('.')[0];
        if (Array.IndexOf(DeviceNames, stem) >= 0)
        {
            return "a segment is a reserved device name";
        }

        return null;
    }

    private static bool IsAlphanumeric(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}
