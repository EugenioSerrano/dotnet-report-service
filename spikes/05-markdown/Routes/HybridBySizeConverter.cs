using System.Text;
using MarkdownSpike.Rendering;
using ReverseMarkdown;

namespace MarkdownSpike.Routes;

/// <summary>
/// Hybrid by size: small documents are converted whole by the library in one
/// call (no stitching), larger ones by the own streaming converter. The choice
/// is made from the header's record count, before any record is read.
/// </summary>
internal static class HybridBySizeConverter
{
    public const int DefaultWholeThreshold = 1000;

    public static bool UseWhole(long recordCount, int threshold, bool forceWhole) => forceWhole || recordCount <= threshold;
}

/// <summary>The whole-document branch: the complete HTML is held and converted at once.</summary>
internal sealed class WholeDocumentConverter(TextWriter output) : IFragmentSink
{
    private readonly StringBuilder document = new();

    public void OnFragment(string html, FragmentKind kind) => document.Append(html);

    public void Complete()
    {
        var converter = new Converter(FragmentStitchingConverter.CreateConfig());
        string markdown = converter.Convert(document.ToString()).Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n');
        document.Clear();
        output.Write(markdown);
        output.Write('\n');
    }
}
