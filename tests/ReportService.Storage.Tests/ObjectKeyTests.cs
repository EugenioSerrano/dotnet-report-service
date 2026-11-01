namespace ReportService.Storage.Tests;

/// <summary>The key grammar: every accepted and refused form, its boundaries and its ordinal identity.</summary>
public sealed class ObjectKeyTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("a.b")]
    [InlineData("a_b-c.d9")]
    [InlineData("a..b")]
    [InlineData("console")]
    [InlineData("com10")]
    [InlineData("conx.txt")]
    [InlineData("staging/probe/api/0123456789abcdef0123456789abcdef")]
    [InlineData("templates/consumer-a/invoice/1.2.3/0123456789abcdef")]
    public void AcceptedKeysKeepTheirTextExactly(string value)
    {
        var key = ObjectKey.Create(value);

        Assert.Equal(value, key.ToString());
        Assert.False(key.IsEmpty);
        Assert.Equal(value.Split('/'), key.Segments);
    }

    [Theory]
    [InlineData("con")]
    [InlineData("prn")]
    [InlineData("aux")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("com9")]
    [InlineData("lpt1")]
    [InlineData("lpt9")]
    [InlineData("con.txt")]
    [InlineData("nul.tar.gz")]
    [InlineData("staging/aux/object")]
    public void DeviceNamesAreRefusedWithOrWithoutAnExtension(string value)
    {
        Assert.False(ObjectKey.TryCreate(value, out var key, out var reason));

        Assert.True(key.IsEmpty);
        Assert.Equal("a segment is a reserved device name", reason);
    }

    [Theory]
    [InlineData(null, "the key is empty")]
    [InlineData("", "the key is empty")]
    [InlineData("a/", "the key starts or ends with '/'")]
    [InlineData(".", "a segment is '.' or '..'")]
    [InlineData("a.", "a segment does not start and end with a lower-case letter or a digit")]
    [InlineData("_a", "a segment does not start and end with a lower-case letter or a digit")]
    [InlineData("Abc", "a segment contains an upper-case letter")]
    [InlineData("a*b", "a segment contains a character other than a-z, 0-9, '.', '_' or '-'")]
    [InlineData("a%2fb", "a segment contains a character other than a-z, 0-9, '.', '_' or '-'")]
    public void RefusedKeysGetADeterministicReason(string? value, string reason)
    {
        Assert.False(ObjectKey.TryCreate(value, out _, out var actual));

        Assert.Equal(reason, actual);
    }

    [Fact]
    public void BoundariesAreInclusive()
    {
        var segment = new string('s', ObjectKey.MaxSegmentLength);
        var sixteen = string.Join('/', Enumerable.Repeat("a", ObjectKey.MaxSegments));
        var full = string.Join('/', Enumerable.Repeat(new string('b', 127), 4));

        Assert.True(ObjectKey.TryCreate(segment, out _, out _));
        Assert.True(ObjectKey.TryCreate(sixteen, out _, out _));
        Assert.Equal(ObjectKey.MaxLength - 1, full.Length);
        Assert.True(ObjectKey.TryCreate(full[..^1] + "cd", out var atLimit, out _));
        Assert.Equal(ObjectKey.MaxLength, atLimit.ToString().Length);
    }

    [Fact]
    public void CreateThrowsWithTheReasonAndNeverTheValue()
    {
        var error = Assert.Throws<InvalidObjectKeyException>(() => ObjectKey.Create("Planted/Secret"));

        Assert.Equal("a segment contains an upper-case letter", error.Reason);
        Assert.Equal("The object key is not valid: a segment contains an upper-case letter.", error.Message);
        Assert.DoesNotContain("Planted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromSegmentsChecksEachSegment()
    {
        Assert.Equal("staging/probe/api", ObjectKey.FromSegments("staging", "probe", "api").ToString());
        Assert.Equal("a segment is missing or contains '/'", Assert.Throws<InvalidObjectKeyException>(() => ObjectKey.FromSegments("a/b", "c")).Reason);
        Assert.Throws<InvalidObjectKeyException>(() => ObjectKey.FromSegments("a", null!));
        Assert.Throws<ArgumentNullException>(() => ObjectKey.FromSegments(null!));
        Assert.Throws<InvalidObjectKeyException>(() => ObjectKey.FromSegments("UPPER"));
    }

    [Fact]
    public void DefaultKeyIsEmpty()
    {
        ObjectKey key = default;

        Assert.True(key.IsEmpty);
        Assert.Equal(string.Empty, key.ToString());
        Assert.Empty(key.Segments);
        Assert.Equal(0, key.GetHashCode());
    }

    [Fact]
    public void EqualityAndOrderAreOrdinal()
    {
        var file = ObjectKey.Create("a.x");
        var nested = ObjectKey.Create("a/b");
        var same = ObjectKey.Create("a/b");

        Assert.True(nested == same);
        Assert.False(nested != same);
        Assert.True(nested.Equals((object)same));
        Assert.False(nested.Equals("a/b"));
        Assert.Equal(nested.GetHashCode(), same.GetHashCode());
        Assert.True(file < nested);
        Assert.True(file <= nested);
        Assert.True(nested > file);
        Assert.True(nested >= same);
        Assert.Equal(0, nested.CompareTo(same));
        Assert.Equal([file, nested], new[] { nested, file }.Order().ToArray());
    }

    [Theory]
    [InlineData("staging", true)]
    [InlineData("staging/probe", true)]
    [InlineData("staging/*", false)]
    [InlineData("../staging", false)]
    [InlineData("", false)]
    public void PrefixesFollowTheKeyGrammar(string prefix, bool valid)
    {
        Assert.Equal(valid, ObjectKey.IsValidPrefix(prefix, out var reason));
        Assert.Equal(valid, reason.Length == 0);
    }
}
