using MarkdownSpike.Rendering;

namespace MarkdownSpike.Routes;

/// <summary>
/// Own streaming converter: every fragment goes through one tokenizer and one
/// writer whose state survives fragment boundaries. Nothing but the current
/// tag, text run, block and table row is held.
/// </summary>
internal sealed class StreamingMarkdownConverter : IFragmentSink
{
    private readonly HtmlTokenizer tokenizer = new();
    private readonly StreamingMarkdownWriter writer;
    private readonly Action<HtmlToken> onToken;

    public StreamingMarkdownConverter(TextWriter output)
    {
        writer = new StreamingMarkdownWriter(output);
        onToken = writer.OnToken;
    }

    /// <summary>What the conversion dropped: element and attribute names, comments, declarations.</summary>
    public SortedDictionary<string, long> Losses
    {
        get
        {
            var all = new SortedDictionary<string, long>(writer.Losses, StringComparer.Ordinal);
            foreach ((string key, long count) in tokenizer.Skipped)
            {
                all[key] = count;
            }

            return all;
        }
    }

    public void OnFragment(string html, FragmentKind kind) => tokenizer.Feed(html, onToken);

    public void Complete()
    {
        tokenizer.Finish(onToken);
        writer.Finish();
    }
}
