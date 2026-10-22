using System.Net;
using System.Text;

namespace MarkdownSpike.Routes;

internal enum HtmlTokenKind
{
    StartTag,
    EndTag,
    Text,
}

/// <summary>One tag or one text run. <see cref="Raw"/> is the source markup exactly as read.</summary>
internal sealed class HtmlToken
{
    public required HtmlTokenKind Kind { get; init; }

    /// <summary>Lower-case element name (tags only).</summary>
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; init; } = [];

    public bool SelfClosing { get; init; }

    /// <summary>Decoded text (text runs only).</summary>
    public string Text { get; init; } = string.Empty;

    public required string Raw { get; init; }

    public string? Attribute(string name)
    {
        foreach (KeyValuePair<string, string> pair in Attributes)
        {
            if (pair.Key == name)
            {
                return pair.Value;
            }
        }

        return null;
    }
}

/// <summary>
/// Forward-only HTML tokenizer. Input arrives in fragments; partial tags and
/// text runs are carried across fragment boundaries. Comments, declarations
/// such as the document type, and the content of style and script elements are
/// skipped and counted. Memory is bounded by one tag and one text run (a text
/// run longer than 64 KiB is emitted in pieces).
/// </summary>
internal sealed class HtmlTokenizer
{
    private const int MaxTextRun = 64 * 1024;
    private const int RawTailLength = 64;

    private readonly StringBuilder text = new();
    private readonly StringBuilder tag = new();
    private readonly StringBuilder rawTail = new();
    private State state = State.Text;
    private char quote;
    private int dashes;
    private string rawTextElement = string.Empty;

    private enum State
    {
        Text,
        TagOpen,
        Tag,
        Declaration,
        Comment,
        RawText,
    }

    /// <summary>Counts of what was skipped: "comment", "declaration", "style content chars", ...</summary>
    public Dictionary<string, long> Skipped { get; } = new(StringComparer.Ordinal);

    public void Feed(string fragment, Action<HtmlToken> sink)
    {
        foreach (char c in fragment)
        {
            Step(c, sink);
        }
    }

    /// <summary>Emits the last text run; fails if a tag is left open.</summary>
    public void Finish(Action<HtmlToken> sink)
    {
        if (state is State.TagOpen)
        {
            text.Append('<');
            state = State.Text;
        }

        if (state is not State.Text)
        {
            throw new InvalidDataException($"The HTML ended inside a {state}.");
        }

        FlushText(sink, text.Length);
    }

    private static bool IsVoidOrSelfClosing(HtmlToken token) => token.SelfClosing || IsVoid(token.Name);

    /// <summary>Elements that never have content or an end tag.</summary>
    public static bool IsVoid(string name) => name is "area" or "base" or "br" or "col" or "embed" or "hr" or "img"
        or "input" or "link" or "meta" or "source" or "track" or "wbr";

    private void Step(char c, Action<HtmlToken> sink)
    {
        switch (state)
        {
            case State.Text:
                if (c == '<')
                {
                    state = State.TagOpen;
                }
                else
                {
                    text.Append(c);
                    if (text.Length >= MaxTextRun)
                    {
                        FlushText(sink, SafeSplit());
                    }
                }

                break;

            case State.TagOpen:
                if (char.IsAsciiLetter(c) || c == '/')
                {
                    FlushText(sink, text.Length);
                    tag.Clear().Append(c);
                    quote = '\0';
                    state = State.Tag;
                }
                else if (c == '!')
                {
                    FlushText(sink, text.Length);
                    tag.Clear().Append(c);
                    state = State.Declaration;
                }
                else if (c != '<')
                {
                    text.Append('<').Append(c);
                    state = State.Text;
                }
                else
                {
                    text.Append('<');
                }

                break;

            case State.Declaration:
                tag.Append(c);
                if (tag.Length == 3 && tag[1] == '-' && tag[2] == '-')
                {
                    dashes = 0;
                    state = State.Comment;
                }
                else if (c == '>')
                {
                    Count("declaration");
                    state = State.Text;
                }

                break;

            case State.Comment:
                if (c == '>' && dashes >= 2)
                {
                    Count("comment");
                    state = State.Text;
                }
                else
                {
                    dashes = c == '-' ? dashes + 1 : 0;
                }

                break;

            case State.Tag:
                if (quote != '\0')
                {
                    tag.Append(c);
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                    tag.Append(c);
                }
                else if (c == '>')
                {
                    EmitTag(sink);
                }
                else
                {
                    tag.Append(c);
                }

                break;

            case State.RawText:
                Count(rawTextElement + " content chars");
                rawTail.Append(c);
                if (rawTail.Length > RawTailLength)
                {
                    rawTail.Remove(0, rawTail.Length - RawTailLength);
                }

                if (c == '>' && EndsWithEndTag())
                {
                    sink(new HtmlToken { Kind = HtmlTokenKind.EndTag, Name = rawTextElement, Raw = $"</{rawTextElement}>" });
                    rawTail.Clear();
                    state = State.Text;
                }

                break;
        }
    }

    private bool EndsWithEndTag()
    {
        string tail = rawTail.ToString();
        int open = tail.LastIndexOf("</", StringComparison.Ordinal);
        if (open < 0)
        {
            return false;
        }

        string inside = tail[(open + 2)..^1].Trim();
        return string.Equals(inside, rawTextElement, StringComparison.OrdinalIgnoreCase);
    }

    private void EmitTag(Action<HtmlToken> sink)
    {
        string body = tag.ToString();
        string raw = "<" + body + ">";
        state = State.Text;

        if (body[0] == '/')
        {
            int nameEnd = 1;
            while (nameEnd < body.Length && !char.IsWhiteSpace(body[nameEnd]))
            {
                nameEnd++;
            }

            sink(new HtmlToken { Kind = HtmlTokenKind.EndTag, Name = body[1..nameEnd].ToLowerInvariant(), Raw = raw });
            return;
        }

        int i = 0;
        while (i < body.Length && !char.IsWhiteSpace(body[i]) && body[i] != '/')
        {
            i++;
        }

        string name = body[..i].ToLowerInvariant();
        var attributes = new List<KeyValuePair<string, string>>();
        while (i < body.Length)
        {
            while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == '/'))
            {
                i++;
            }

            int nameStart = i;
            while (i < body.Length && !char.IsWhiteSpace(body[i]) && body[i] is not ('=' or '/'))
            {
                i++;
            }

            if (i == nameStart)
            {
                break;
            }

            string attributeName = body[nameStart..i].ToLowerInvariant();
            while (i < body.Length && char.IsWhiteSpace(body[i]))
            {
                i++;
            }

            string value = string.Empty;
            if (i < body.Length && body[i] == '=')
            {
                i++;
                while (i < body.Length && char.IsWhiteSpace(body[i]))
                {
                    i++;
                }

                if (i < body.Length && body[i] is '"' or '\'')
                {
                    char q = body[i++];
                    int valueStart = i;
                    while (i < body.Length && body[i] != q)
                    {
                        i++;
                    }

                    value = body[valueStart..i];
                    i = Math.Min(i + 1, body.Length);
                }
                else
                {
                    int valueStart = i;
                    while (i < body.Length && !char.IsWhiteSpace(body[i]))
                    {
                        i++;
                    }

                    value = body[valueStart..i];
                }
            }

            attributes.Add(new(attributeName, WebUtility.HtmlDecode(value)));
        }

        bool selfClosing = body.TrimEnd().EndsWith('/');
        var token = new HtmlToken { Kind = HtmlTokenKind.StartTag, Name = name, Attributes = attributes, SelfClosing = selfClosing, Raw = raw };
        sink(token);
        if (!IsVoidOrSelfClosing(token) && name is "style" or "script")
        {
            Count(name + " element");
            rawTextElement = name;
            rawTail.Clear();
            state = State.RawText;
        }
    }

    /// <summary>Where a long text run may be cut: never inside an entity or a surrogate pair.</summary>
    private int SafeSplit()
    {
        int split = text.Length;
        for (int back = split - 1; back >= 0 && back >= split - 32; back--)
        {
            if (text[back] == ';')
            {
                break;
            }

            if (text[back] == '&')
            {
                split = back;
                break;
            }
        }

        if (split > 0 && char.IsHighSurrogate(text[split - 1]))
        {
            split--;
        }

        return split;
    }

    private void FlushText(Action<HtmlToken> sink, int length)
    {
        if (length == 0)
        {
            return;
        }

        string raw = text.ToString(0, length);
        text.Remove(0, length);
        sink(new HtmlToken { Kind = HtmlTokenKind.Text, Text = WebUtility.HtmlDecode(raw), Raw = raw });
    }

    private void Count(string what) => Skipped[what] = Skipped.GetValueOrDefault(what) + 1;
}
