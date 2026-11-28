using System.Net;
using System.Text;

namespace ReportService.Templates;

/// <summary>
/// Reads the composed HTML as it is written, in chunks of any size, and checks the source
/// of every image: the <c>src</c> of an <c>img</c> (and of an <c>image</c>, which an HTML
/// parser reads as <c>img</c>) and the <c>href</c> or any prefixed <c>href</c> of an
/// <c>image</c>. Each such value is decoded (character references resolved), prepared as a
/// browser prepares a URL (tabs and line breaks removed, leading and trailing controls and
/// spaces stripped, a backslash read as a slash for the scheme) and classified:
/// <list type="bullet">
/// <item>a base64 data URI of a listed image media type passes;</item>
/// <item>a relative reference passes unless the policy is <see cref="ImageSourcePolicy.Pdf"/>;</item>
/// <item>every other value, an empty one included, is refused.</item>
/// </list>
/// The scanner keeps its state between chunks and holds no value: its memory does not grow
/// with the text. A value it cannot classify is refused.
/// </summary>
internal sealed class ImageSourceScanner(ImageSourcePolicy policy)
{
    private const int MaximumNameLength = 64;
    private const int MaximumReferenceLength = 32;
    private const int MaximumPrefixLength = 256;

    private static readonly string[] ImageMediaTypes =
        ["image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml"];

    private readonly StringBuilder tagName = new(MaximumNameLength);
    private readonly StringBuilder attributeName = new(MaximumNameLength);
    private readonly StringBuilder reference = new(MaximumReferenceLength);
    private readonly ImageSourceValue value = new();
    private State state = State.Data;
    private string rawTextEnd = string.Empty;
    private int rawTextMatched;
    private int commentDashes;
    private int markupCharacters;
    private bool markupComment;
    private bool commentBang;
    private char quote;
    private bool watched;

    private enum State
    {
        Data,
        TagOpen,
        TagName,
        Bogus,
        MarkupDeclaration,
        Comment,
        BeforeAttributeName,
        AttributeName,
        AfterAttributeName,
        BeforeAttributeValue,
        AttributeValueQuoted,
        AttributeValueUnquoted,
        AfterAttributeValueQuoted,
        RawText,
    }

    /// <summary>Reads one chunk; answers the attribute whose value was refused, or <see langword="null"/>.</summary>
    /// <param name="text">The chunk, as the writer received it.</param>
    public string? Feed(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (Step(character) is { } refused)
            {
                return refused;
            }
        }

        return null;
    }

    /// <summary>
    /// Ends the text: answers the attribute of an image source whose value was still open, or
    /// <see langword="null"/>. An unterminated source is refused rather than guessed.
    /// </summary>
    public string? Complete() =>
        state is State.AttributeValueQuoted or State.AttributeValueUnquoted && watched
            ? attributeName.ToString()
            : null;

    private string? Step(char character)
    {
        switch (state)
        {
            case State.Data:
                if (character == '<')
                {
                    state = State.TagOpen;
                }

                return null;

            case State.TagOpen:
                if (IsAsciiLetter(character))
                {
                    tagName.Clear();
                    tagName.Append(char.ToLowerInvariant(character));
                    state = State.TagName;
                }
                else if (character == '!')
                {
                    markupCharacters = 0;
                    markupComment = true;
                    state = State.MarkupDeclaration;
                }
                else if (character is '/' or '?')
                {
                    state = State.Bogus;
                }
                else
                {
                    state = character == '<' ? State.TagOpen : State.Data;
                }

                return null;

            case State.Bogus:
                if (character == '>')
                {
                    state = State.Data;
                }

                return null;

            case State.MarkupDeclaration:
                if (character == '-' && markupComment)
                {
                    markupCharacters++;
                    if (markupCharacters == 2)
                    {
                        // A comment that closes at once ("<!-->", "<!--->") ends as a browser ends it.
                        commentDashes = 2;
                        commentBang = false;
                        state = State.Comment;
                    }

                    return null;
                }

                markupComment = false;
                if (character == '>')
                {
                    state = State.Data;
                }
                else
                {
                    state = State.Bogus;
                }

                return null;

            case State.Comment:
                if (character == '-')
                {
                    commentDashes++;
                    commentBang = false;
                }
                else if (character == '!' && commentDashes >= 2 && !commentBang)
                {
                    commentBang = true;
                }
                else if (character == '>' && commentDashes >= 2)
                {
                    state = State.Data;
                }
                else
                {
                    commentDashes = 0;
                    commentBang = false;
                }

                return null;

            case State.TagName:
                if (IsWhitespace(character) || character == '/')
                {
                    state = State.BeforeAttributeName;
                }
                else if (character == '>')
                {
                    EndStartTag();
                }
                else if (tagName.Length < MaximumNameLength)
                {
                    tagName.Append(char.ToLowerInvariant(character));
                }

                return null;

            case State.BeforeAttributeName:
                if (IsWhitespace(character) || character == '/')
                {
                    return null;
                }

                if (character == '>')
                {
                    EndStartTag();
                    return null;
                }

                attributeName.Clear();
                attributeName.Append(char.ToLowerInvariant(character));
                state = State.AttributeName;
                return null;

            case State.AttributeName:
                if (IsWhitespace(character))
                {
                    state = State.AfterAttributeName;
                    return null;
                }

                if (character == '/')
                {
                    state = State.BeforeAttributeName;
                    return EndValuelessAttribute();
                }

                if (character == '>')
                {
                    var refused = EndValuelessAttribute();
                    EndStartTag();
                    return refused;
                }

                if (character == '=')
                {
                    state = State.BeforeAttributeValue;
                    return null;
                }

                if (attributeName.Length < MaximumNameLength)
                {
                    attributeName.Append(char.ToLowerInvariant(character));
                }

                return null;

            case State.AfterAttributeName:
                if (IsWhitespace(character))
                {
                    return null;
                }

                if (character == '=')
                {
                    state = State.BeforeAttributeValue;
                    return null;
                }

                {
                    var refused = EndValuelessAttribute();
                    if (character == '/')
                    {
                        state = State.BeforeAttributeName;
                    }
                    else if (character == '>')
                    {
                        EndStartTag();
                    }
                    else
                    {
                        attributeName.Clear();
                        attributeName.Append(char.ToLowerInvariant(character));
                        state = State.AttributeName;
                    }

                    return refused;
                }

            case State.BeforeAttributeValue:
                if (IsWhitespace(character))
                {
                    return null;
                }

                BeginValue();
                if (character is '"' or '\'')
                {
                    quote = character;
                    state = State.AttributeValueQuoted;
                    return null;
                }

                if (character == '>')
                {
                    var refused = EndValue();
                    EndStartTag();
                    return refused;
                }

                state = State.AttributeValueUnquoted;
                ReadValue(character);
                return null;

            case State.AttributeValueQuoted:
                if (character == quote)
                {
                    state = State.AfterAttributeValueQuoted;
                    return EndValue();
                }

                ReadValue(character);
                return null;

            case State.AttributeValueUnquoted:
                if (IsWhitespace(character))
                {
                    state = State.BeforeAttributeName;
                    return EndValue();
                }

                if (character == '>')
                {
                    var refused = EndValue();
                    EndStartTag();
                    return refused;
                }

                ReadValue(character);
                return null;

            case State.AfterAttributeValueQuoted:
                if (IsWhitespace(character) || character == '/')
                {
                    state = State.BeforeAttributeName;
                }
                else if (character == '>')
                {
                    EndStartTag();
                }
                else
                {
                    attributeName.Clear();
                    attributeName.Append(char.ToLowerInvariant(character));
                    state = State.AttributeName;
                }

                return null;

            case State.RawText:
                ReadRawText(character);
                return null;

            default:
                return null;
        }
    }

    private void EndStartTag()
    {
        var name = tagName.ToString();
        if (name is "script" or "style")
        {
            rawTextEnd = "</" + name;
            rawTextMatched = 0;
            state = State.RawText;
            return;
        }

        state = State.Data;
    }

    private void ReadRawText(char character)
    {
        var expected = rawTextEnd[rawTextMatched];
        if (char.ToLowerInvariant(character) == expected)
        {
            rawTextMatched++;
            if (rawTextMatched == rawTextEnd.Length)
            {
                // The end tag's name is read; the rest of it up to '>' is skipped.
                state = State.Bogus;
            }

            return;
        }

        rawTextMatched = character == '<' ? 1 : 0;
    }

    private bool IsWatched()
    {
        var tag = LocalName(tagName.ToString());
        var attribute = attributeName.ToString();
        if (attribute == "src")
        {
            return tag is "img" or "image";
        }

        return tag == "image" && LocalName(attribute) == "href";
    }

    private string? EndValuelessAttribute()
    {
        // An image source written without a value is an empty value, which is refused.
        return IsWatched() ? attributeName.ToString() : null;
    }

    private void BeginValue()
    {
        watched = IsWatched();
        reference.Clear();
        if (watched)
        {
            value.Reset();
        }
    }

    private void ReadValue(char character)
    {
        if (!watched)
        {
            return;
        }

        if (reference.Length > 0)
        {
            if (character == ';')
            {
                reference.Append(';');
                var text = reference.ToString();
                reference.Clear();
                var decoded = WebUtility.HtmlDecode(text);
                value.Read(decoded);
                return;
            }

            if ((IsAsciiLetter(character) || char.IsAsciiDigit(character) || character == '#')
                && reference.Length < MaximumReferenceLength)
            {
                reference.Append(character);
                return;
            }

            FlushReference();
        }

        if (character == '&')
        {
            reference.Append('&');
            return;
        }

        value.Read(character);
    }

    private void FlushReference()
    {
        if (reference.Length > 0)
        {
            value.Read(reference.ToString());
            reference.Clear();
        }
    }

    private string? EndValue()
    {
        if (!watched)
        {
            return null;
        }

        watched = false;
        FlushReference();
        return value.Classify(policy) ? null : attributeName.ToString();
    }

    private static string LocalName(string name)
    {
        var colon = name.LastIndexOf(':');
        return colon < 0 ? name : name[(colon + 1)..];
    }

    private static bool IsAsciiLetter(char character) => char.IsAsciiLetter(character);

    private static bool IsWhitespace(char character) => character is ' ' or '\t' or '\n' or '\r' or '\f';

    /// <summary>One image source's value, read character by character without being kept.</summary>
    internal sealed class ImageSourceValue
    {
        private readonly StringBuilder prefix = new(MaximumPrefixLength);
        private int length;
        private int pendingSpaces;
        private bool innerSpace;
        private bool schemeDecided;
        private bool hasScheme;
        private int leadingSlashes;
        private bool leadingSlashesDone;
        private bool prefixDone;
        private bool prefixValid;
        private bool bodyValid;
        private int bodyLength;
        private int padding;

        public void Reset()
        {
            prefix.Clear();
            length = 0;
            pendingSpaces = 0;
            innerSpace = false;
            schemeDecided = false;
            hasScheme = false;
            leadingSlashes = 0;
            leadingSlashesDone = false;
            prefixDone = false;
            prefixValid = false;
            bodyValid = true;
            bodyLength = 0;
            padding = 0;
        }

        public void Read(string text)
        {
            foreach (var character in text)
            {
                Read(character);
            }
        }

        public void Read(char character)
        {
            if (character is '\t' or '\n' or '\r')
            {
                return;
            }

            if (character <= ' ')
            {
                if (length > 0)
                {
                    pendingSpaces++;
                }

                return;
            }

            if (pendingSpaces > 0)
            {
                innerSpace = true;
                pendingSpaces = 0;
            }

            var position = length;
            length++;
            ReadScheme(character == '\\' ? '/' : character, position);
            ReadDataUri(character);
        }

        private void ReadScheme(char character, int position)
        {
            if (!leadingSlashesDone)
            {
                if (character == '/' && leadingSlashes < 2)
                {
                    leadingSlashes++;
                }
                else
                {
                    leadingSlashesDone = true;
                }

                if (leadingSlashes == 2)
                {
                    leadingSlashesDone = true;
                }
            }

            if (schemeDecided)
            {
                return;
            }

            if (position == 0)
            {
                if (!char.IsAsciiLetter(character))
                {
                    schemeDecided = true;
                }

                return;
            }

            if (character == ':')
            {
                hasScheme = true;
                schemeDecided = true;
            }
            else if (!(char.IsAsciiLetterOrDigit(character) || character is '+' or '.' or '-'))
            {
                schemeDecided = true;
            }
        }

        private void ReadDataUri(char character)
        {
            if (!prefixDone)
            {
                if (character == ',')
                {
                    prefixDone = true;
                    prefixValid = IsImagePrefix(prefix.ToString());
                    return;
                }

                if (prefix.Length >= MaximumPrefixLength)
                {
                    prefixDone = true;
                    prefixValid = false;
                    bodyValid = false;
                    return;
                }

                prefix.Append(character);
                return;
            }

            if (!prefixValid || !bodyValid)
            {
                return;
            }

            bodyLength++;
            if (character == '=')
            {
                padding++;
                if (padding > 2)
                {
                    bodyValid = false;
                }

                return;
            }

            if (padding > 0 || !IsBase64(character))
            {
                bodyValid = false;
            }
        }

        /// <summary>Whether the value read is an image data URI as the data contract defines it.</summary>
        public bool IsImageDataUri =>
            length > 0 && prefixDone && prefixValid && bodyValid && !innerSpace && bodyLength % 4 == 0;

        public bool Classify(ImageSourcePolicy policy)
        {
            if (length == 0)
            {
                return false;
            }

            if (IsImageDataUri)
            {
                return true;
            }

            var relative = !hasScheme && leadingSlashes < 2;
            return relative && policy != ImageSourcePolicy.Pdf;
        }

        private static bool IsBase64(char character) =>
            char.IsAsciiLetterOrDigit(character) || character is '+' or '/';

        private static bool IsImagePrefix(string text)
        {
            // data:<media type>[;name=value]*;base64 — scheme, type and token in any case.
            if (!text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var segments = text[5..].Split(';');
            if (segments.Length < 2)
            {
                return false;
            }

            if (!ImageMediaTypes.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!segments[^1].Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (var i = 1; i < segments.Length - 1; i++)
            {
                var equals = segments[i].IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
