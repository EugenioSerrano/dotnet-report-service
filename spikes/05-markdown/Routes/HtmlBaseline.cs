using MarkdownSpike.Rendering;

namespace MarkdownSpike.Routes;

/// <summary>Writes the rendered HTML fragments unchanged: the shared cost of every HTML-based route.</summary>
internal sealed class HtmlBaseline(TextWriter output) : IFragmentSink
{
    public void OnFragment(string html, FragmentKind kind) => output.Write(html);

    public void Complete()
    {
    }
}
