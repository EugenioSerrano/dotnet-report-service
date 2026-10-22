using System.Text;

namespace MarkdownSpike.Rendering;

/// <summary>Where a fragment sits in the document.</summary>
internal enum FragmentKind
{
    /// <summary>The document start part.</summary>
    Header,

    /// <summary>Every part produced for a fixed number of consecutive records.</summary>
    Block,

    /// <summary>The document end part.</summary>
    Closing,
}

/// <summary>Receives the rendered HTML one fragment at a time.</summary>
internal interface IFragmentSink
{
    void OnFragment(string html, FragmentKind kind);

    /// <summary>Called once after the closing fragment.</summary>
    void Complete();
}

/// <summary>
/// Groups rendered parts into fragments: the header part, then one fragment per
/// block of consecutive records (cut by record count, ignoring group
/// boundaries on purpose), then the closing part. A group end goes into the
/// block that holds the group's last record. Only the current block is held.
/// </summary>
internal sealed class BlockAssembler(int blockSize, IFragmentSink sink) : IPartSink
{
    private readonly StringBuilder current = new();
    private long currentBlock = -1;

    public int Blocks { get; private set; }

    public void OnPart(PartKind kind, string text, long recordIndex)
    {
        switch (kind)
        {
            case PartKind.DocumentStart:
                sink.OnFragment(text, FragmentKind.Header);
                break;
            case PartKind.DocumentEnd:
                Flush();
                sink.OnFragment(text, FragmentKind.Closing);
                sink.Complete();
                break;
            default:
                long block = recordIndex / blockSize;
                if (block != currentBlock)
                {
                    Flush();
                    currentBlock = block;
                }

                current.Append(text);
                break;
        }
    }

    private void Flush()
    {
        if (current.Length == 0)
        {
            return;
        }

        sink.OnFragment(current.ToString(), FragmentKind.Block);
        Blocks++;
        current.Clear();
    }
}
