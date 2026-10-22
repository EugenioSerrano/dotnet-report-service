using System.Text;
using HandlebarsDotNet;

namespace StreamingDataSpike.Rendering;

/// <summary>
/// The escaping policy as the Handlebars text encoder: every <c>{{ }}</c>
/// output goes through it (<c>{{{ }}}</c> would bypass it and is not used).
/// </summary>
internal sealed class HtmlPolicyHandlebarsEncoder : ITextEncoder
{
    public void Encode(StringBuilder? text, TextWriter target)
    {
        if (text is not null)
        {
            target.Write(HtmlPolicy.Encode(text.ToString()));
        }
    }

    public void Encode(string? text, TextWriter target)
    {
        if (text is not null)
        {
            target.Write(HtmlPolicy.Encode(text));
        }
    }

    public void Encode<T>(T? text, TextWriter target)
        where T : IEnumerator<char>
    {
        if (text is null)
        {
            return;
        }

        var builder = new StringBuilder();
        while (text.MoveNext())
        {
            builder.Append(text.Current);
        }

        target.Write(HtmlPolicy.Encode(builder.ToString()));
    }
}
