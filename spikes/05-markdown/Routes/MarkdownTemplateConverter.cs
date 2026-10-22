using MarkdownSpike.Rendering;

namespace MarkdownSpike.Routes;

/// <summary>
/// Markdown template parts rendered by the engine: each rendered part is
/// written straight to the output, so no HTML exists on this route.
/// </summary>
internal sealed class MarkdownTemplateConverter(TextWriter output) : IPartSink
{
    public void OnPart(PartKind kind, string text, long recordIndex) => output.Write(text);
}
