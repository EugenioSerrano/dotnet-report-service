using System.Text.Encodings.Web;

namespace StreamingDataSpike.Rendering;

/// <summary>
/// The escaping policy as a <see cref="TextEncoder"/>, for the Liquid engine:
/// every <c>{{ }}</c> output goes through it. The per-character rule is the
/// one of <see cref="HtmlPolicy"/>; invalid UTF-16 is replaced by U+FFFD by the
/// base class, as the policy does.
/// </summary>
internal sealed class HtmlPolicyTextEncoder : TextEncoder
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly HtmlPolicyTextEncoder Instance = new();

    // "&#1114111;" is the longest reference the policy writes.
    public override int MaxOutputCharactersPerInputCharacter => 10;

    public override bool WillEncode(int unicodeScalar) => HtmlPolicy.WillEncode(unicodeScalar);

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (var i = 0; i < textLength; i++)
        {
            var c = text[i];
            if (char.IsSurrogate(c) || HtmlPolicy.WillEncode(c))
            {
                return i;
            }
        }

        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        var text = HtmlPolicy.EncodeScalar(unicodeScalar);
        if (text.Length > bufferLength)
        {
            numberOfCharactersWritten = 0;
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            buffer[i] = text[i];
        }

        numberOfCharactersWritten = text.Length;
        return true;
    }
}
