using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReportService.Testing.Fixtures;

/// <summary>
/// The bound family: the six snapshots whose record 2 carries a description of
/// exactly 262 144 decoded UTF-8 bytes. The words are produced by an explicit
/// length rule, not by the delivered fixture vocabulary: 12 210 seven-letter and
/// 23 495 six-letter ASCII words for the one-byte member, 6 105 four- and 29 600
/// three-character runs for the two-byte member, and 4 070 three- and 31 635
/// two-character runs for the three-byte member. With 35 704 single spaces every
/// member holds 35 705 words and 226 440 letters, so 226 440 + 35 704 = 262 144
/// bytes. The over-bound twins append one character to the last word (262 145,
/// 262 146 and 262 147 bytes).
/// <para>
/// The snapshots are pilot-shaped: the root has contractVersion, header and
/// items; the header is the pilot schema's seven members; the three records are
/// the five-member item shape. The bytes are deterministic and their SHA-256 is
/// recorded beside the fixture by the library's writer.
/// </para>
/// </summary>
public static class BoundFamily
{
    /// <summary>The decoded size of the description every at-the-bound member holds.</summary>
    public const int RowBoundBytes = 262_144;

    /// <summary>The single ASCII spaces every member holds.</summary>
    public const int Spaces = 35_704;

    /// <summary>The words every member holds.</summary>
    public const int Words = 35_705;

    /// <summary>The one-byte at-the-bound member.</summary>
    public const string Ascii = "boundAscii";

    /// <summary>The two-byte at-the-bound member.</summary>
    public const string TwoByte = "boundTwoByte";

    /// <summary>The three-byte at-the-bound member.</summary>
    public const string ThreeByte = "boundThreeByte";

    /// <summary>The one-byte over-bound twin.</summary>
    public const string OverAscii = "overAscii";

    /// <summary>The two-byte over-bound twin.</summary>
    public const string OverTwoByte = "overTwoByte";

    /// <summary>The three-byte over-bound twin.</summary>
    public const string OverThreeByte = "overThreeByte";

    /// <summary>The family's ids, in ordinal order.</summary>
    public static IReadOnlyList<string> Ids { get; } =
    [
        Ascii,
        ThreeByte,
        TwoByte,
        OverAscii,
        OverThreeByte,
        OverTwoByte,
    ];

    private static readonly string[] SevenLetter = ["service", "network", "balance", "journey", "freight"];

    private static readonly string[] SixLetter = ["office", "sensor", "wiring", "nozzle", "gasket"];

    private const int AsciiLongWords = 12_210;
    private const int AsciiShortWords = 23_495;
    private const int TwoByteLongWords = 6_105;
    private const int TwoByteShortWords = 29_600;
    private const int ThreeByteLongWords = 4_070;
    private const int ThreeByteShortWords = 31_635;

    private static readonly JsonSerializerOptions Writing = new()
    {
        // The snapshot is read by a JSON reader, never embedded in a page, so the
        // accented and three-byte letters stay the characters they are. The
        // library's other fixtures use the same writer.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Whether <paramref name="id"/> is one of the family's six fixtures.</summary>
    public static bool IsBound(string id) => Ids.Contains(id, StringComparer.Ordinal);

    /// <summary>The exact description the member <paramref name="id"/> carries.</summary>
    public static string Description(string id) => id switch
    {
        Ascii => AsciiText(),
        TwoByte => RepeatedText('é', TwoByteLongWords, 4, TwoByteShortWords, 3),
        ThreeByte => RepeatedText('€', ThreeByteLongWords, 3, ThreeByteShortWords, 2),
        OverAscii => AsciiText() + 'a',
        OverTwoByte => RepeatedText('é', TwoByteLongWords, 4, TwoByteShortWords, 3) + 'é',
        OverThreeByte => RepeatedText('€', ThreeByteLongWords, 3, ThreeByteShortWords, 2) + '€',
        _ => throw new ArgumentException($"Unknown bound fixture id '{id}'.", nameof(id)),
    };

    /// <summary>The decoded UTF-8 bytes of the member's description.</summary>
    public static int Bytes(string id) => Encoding.UTF8.GetByteCount(Description(id));

    /// <summary>The characters (letters and spaces) of the member's description.</summary>
    public static int Characters(string id) => Description(id).Length;

    /// <summary>The letters (characters other than the spaces) of the member's description.</summary>
    public static int Letters(string id) => id switch
    {
        Ascii => 226_440,
        TwoByte => 113_220,
        ThreeByte => 75_480,
        OverAscii => 226_441,
        OverTwoByte => 113_221,
        OverThreeByte => 75_481,
        _ => throw new ArgumentException($"Unknown bound fixture id '{id}'.", nameof(id)),
    };

    /// <summary>The SHA-256 of the member's exact source string, lower-case hexadecimal.</summary>
    public static string SourceSha256(string id) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Description(id))));

    /// <summary>The pilot-shaped snapshot of the member: three records, the bound one as record 2.</summary>
    public static SnapshotFixture Snapshot(string id)
    {
        var description = Description(id);
        var header = new JsonObject
        {
            ["datasetId"] = "pilot-simple",
            ["reportTitle"] = "Bound fixture",
            ["generatedAt"] = "2026-01-01T00:00:00Z",
            ["seed"] = 20261022,
            ["currency"] = "ARS",
            ["customer"] = new JsonObject
            {
                ["id"] = "customer-bound",
                ["name"] = "Bound Customer",
            },
            ["totals"] = new JsonObject
            {
                ["recordCount"] = 3,
                ["amount"] = 3.00m,
            },
        };
        var items = new JsonArray
        {
            Record(1, "ordinary one"),
            Record(2, description),
            Record(3, "ordinary three"),
        };
        var root = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["header"] = header,
            ["items"] = items,
        };
        var bytes = Utf8WithoutBom.GetBytes(root.ToJsonString(Writing));
        return new SnapshotFixture(id, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), 3);
    }

    // 12 210 seven-letter words followed by 23 495 six-letter words: the five-word
    // lists are cycled exactly 2 442 and 4 699 times, and the last word ends on a letter.
    private static string AsciiText()
    {
        var builder = new StringBuilder(RowBoundBytes);
        for (var index = 0; index < AsciiLongWords; index++)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(SevenLetter[index % SevenLetter.Length]);
        }

        for (var index = 0; index < AsciiShortWords; index++)
        {
            builder.Append(' ');
            builder.Append(SixLetter[index % SixLetter.Length]);
        }

        return builder.ToString();
    }

    // A run of one letter, then a run of the same letter one character shorter: the
    // last word ends on the letter.
    private static string RepeatedText(char letter, int longWords, int longLength, int shortWords, int shortLength)
    {
        var builder = new StringBuilder((longWords * longLength) + (shortWords * shortLength) + longWords + shortWords - 1);
        for (var index = 0; index < longWords; index++)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(letter, longLength);
        }

        for (var index = 0; index < shortWords; index++)
        {
            builder.Append(' ');
            builder.Append(letter, shortLength);
        }

        return builder.ToString();
    }

    private static JsonObject Record(int sequence, string description) => new()
    {
        ["sequence"] = sequence,
        ["description"] = description,
        ["quantity"] = 1,
        ["unitPrice"] = 1.00m,
        ["amount"] = 1.00m,
    };
}
