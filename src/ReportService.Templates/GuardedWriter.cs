using System.Text;

namespace ReportService.Templates;

/// <summary>
/// The writer a part's output goes through. It counts the characters of one render
/// call and the UTF-8 bytes of the block, and it reads the deadline on every
/// write, so a runaway template is stopped while it composes rather than after:
/// <list type="bullet">
/// <item>a render call above 16 Mi characters, or a block above 32 MiB, ends with
/// <c>QUOTA_EXCEEDED limit=render-output</c>;</item>
/// <item>a block not produced within 60 s ends with <c>RENDER_TIMEOUT</c>.</item>
/// </list>
/// The values are guard values far above the measured cost of admissible data;
/// they exist for a template that misbehaves, not for the data of a job.
/// </summary>
public sealed class GuardedWriter : TextWriter
{
    /// <summary>The most characters one render call may produce.</summary>
    public const int MaximumCharactersPerCall = 16 * 1024 * 1024;

    /// <summary>The most UTF-8 bytes one block may compose.</summary>
    public const long MaximumBlockBytes = 32L * 1024 * 1024;

    /// <summary>How long one block may take to compose.</summary>
    public static readonly TimeSpan MaximumBlockTime = TimeSpan.FromSeconds(60);

    private readonly TextWriter inner;
    private readonly TimeProvider clock;
    private readonly long startedAt;
    private long bytes;
    private int callCharacters;

    /// <summary>Creates a writer over <paramref name="inner"/>, starting the block's deadline now.</summary>
    public GuardedWriter(TextWriter inner, TimeProvider clock)
    {
        this.inner = inner;
        this.clock = clock;
        startedAt = clock.GetTimestamp();
    }

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>How many UTF-8 bytes the block has composed so far.</summary>
    public long BytesWritten => bytes;

    /// <summary>How many characters the current render call has produced so far.</summary>
    public int CallCharacters => callCharacters;

    /// <summary>Starts a new render call of the same block; the block's totals are kept.</summary>
    public void BeginCall() => callCharacters = 0;

    /// <inheritdoc />
    public override void Write(char value) => Write(value.ToString());

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(new string(buffer, index, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer) => Write(new string(buffer));

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        CheckDeadline();
        callCharacters = checked(callCharacters + value.Length);
        if (callCharacters > MaximumCharactersPerCall)
        {
            throw TemplateGuardException.Quota(
                "render-output",
                $"The composed text of one part passed {MaximumCharactersPerCall} characters.");
        }

        var written = Encoding.UTF8.GetByteCount(value);
        if (bytes + written > MaximumBlockBytes)
        {
            throw TemplateGuardException.Quota(
                "render-output",
                $"The composed text of the block passed {MaximumBlockBytes} bytes.");
        }

        bytes += written;
        inner.Write(value);
    }

    /// <inheritdoc />
    public override void WriteLine(string? value) => Write(value + Environment.NewLine);

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <summary>The text the block has composed, when the writer was given a string builder.</summary>
    public override string ToString() => inner.ToString() ?? string.Empty;

    private void CheckDeadline()
    {
        var elapsed = clock.GetElapsedTime(startedAt);
        if (elapsed > MaximumBlockTime)
        {
            throw TemplateGuardException.Timeout(
                $"The composed text of the block was not produced within {MaximumBlockTime.TotalSeconds:0} s.");
        }
    }
}
