using System.Globalization;
using System.Threading.Channels;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// The watching form of <c>render</c>: renders once, then renders again each time a file
/// of the package - or the named data file - changes, until cancelled. Each render prints
/// exactly what a single <c>render</c> prints and a failed render does not stop the
/// watch. Changes arriving close together are rendered once, after a quiet period; a
/// change made during a render leads to one more render after it. Changes under the
/// render's own output directory are never a reason to render.
/// </summary>
/// <param name="tool">The tool that renders.</param>
/// <param name="quietPeriod">How long the package must stay unchanged before a render, or <see langword="null"/> for 300 ms.</param>
public sealed class PackageWatcher(TemplateAuthoringTool tool, TimeSpan? quietPeriod = null)
{
    /// <summary>The line printed once the first render is done and the watch begins.</summary>
    public const string WatchingLine = "watching (Ctrl+C to stop)";

    private readonly TemplateAuthoringTool _tool = tool ?? throw new ArgumentNullException(nameof(tool));

    private readonly TimeSpan _quiet = quietPeriod ?? TimeSpan.FromMilliseconds(300);

    /// <summary>Renders, then watches and renders on change until <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="request">The render request every render uses.</param>
    /// <param name="output">Where each render's lines go, flushed when that render ends.</param>
    /// <param name="cancellationToken">Cancelled to stop watching.</param>
    /// <param name="changes">
    /// The changed paths to react to, or <see langword="null"/> to watch the file system
    /// (the package folder, recursively, and the named data file).
    /// </param>
    /// <param name="usageOutput">Where a first render that is a usage error writes, or <see langword="null"/> for <paramref name="output"/>.</param>
    /// <returns>2 when the first render is a usage error (nothing is watched), else 0 once cancelled.</returns>
    public async Task<int> RunAsync(
        RenderRequest request,
        TextWriter output,
        CancellationToken cancellationToken,
        ChannelReader<string>? changes = null,
        TextWriter? usageOutput = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);

        var outputDirectories = new List<string>();
        Remember(outputDirectories, request);
        int first;
        try
        {
            first = await RenderOnceAsync(request, output, usageOutput, firstRender: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }

        if (first == 2)
        {
            return 2;
        }

        Remember(outputDirectories, request);
        using var fileSystem = changes is null ? new FileSystemChanges(request) : null;
        var reader = changes ?? fileSystem!.Reader;
        await output.WriteLineAsync(WatchingLine).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (true)
            {
                var path = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (IsUnder(outputDirectories, path))
                {
                    continue;
                }

                // Wait for the package to stay unchanged for the quiet period, taking every
                // change that arrived meanwhile into this one render.
                bool more;
                do
                {
                    await Task.Delay(_quiet, cancellationToken).ConfigureAwait(false);
                    more = false;
                    while (reader.TryRead(out var next))
                    {
                        more |= !IsUnder(outputDirectories, next);
                    }
                }
                while (more);

                Remember(outputDirectories, request);
                await RenderOnceAsync(request, output, usageOutput: null, firstRender: false, cancellationToken).ConfigureAwait(false);
                Remember(outputDirectories, request);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (ChannelClosedException)
        {
            return 0;
        }
    }

    private async Task<int> RenderOnceAsync(
        RenderRequest request,
        TextWriter output,
        TextWriter? usageOutput,
        bool firstRender,
        CancellationToken cancellationToken)
    {
        var lines = new StringWriter(CultureInfo.InvariantCulture);
        var code = await _tool.RenderAsync(request, lines, cancellationToken).ConfigureAwait(false);
        var target = firstRender && code == 2 && usageOutput is not null ? usageOutput : output;
        await target.WriteAsync(lines.ToString()).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        return code;
    }

    // The output location is resolved again each time: the manifest's id or version can change.
    private static void Remember(List<string> outputDirectories, RenderRequest request)
    {
        if (TemplateAuthoringTool.PreviewDirectoryOf(request) is { } directory)
        {
            var normalized = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
            if (!outputDirectories.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                outputDirectories.Add(normalized);
            }
        }
    }

    private static bool IsUnder(List<string> outputDirectories, string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var asDirectory = Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;
        return outputDirectories.Exists(directory => asDirectory.StartsWith(directory, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The changed paths of the package folder and the named data file, as the file system reports them.</summary>
    private sealed class FileSystemChanges : IDisposable
    {
        private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        private readonly List<FileSystemWatcher> _watchers = [];

        public FileSystemChanges(RenderRequest request)
        {
            Add(new FileSystemWatcher(Path.GetFullPath(request.PackageDirectory)) { IncludeSubdirectories = true });
            if (request.DataFile is { Length: > 0 } data)
            {
                var full = Path.GetFullPath(data);
                Add(new FileSystemWatcher(Path.GetDirectoryName(full)!, Path.GetFileName(full)));
            }
        }

        public ChannelReader<string> Reader => _channel.Reader;

        public void Dispose()
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _channel.Writer.TryComplete();
        }

        private void Add(FileSystemWatcher watcher)
        {
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.Changed += (_, change) => _channel.Writer.TryWrite(change.FullPath);
            watcher.Created += (_, change) => _channel.Writer.TryWrite(change.FullPath);
            watcher.Deleted += (_, change) => _channel.Writer.TryWrite(change.FullPath);
            watcher.Renamed += (_, change) => _channel.Writer.TryWrite(change.FullPath);

            // A lost notification is treated as a change of the watched folder itself.
            watcher.Error += (_, _) => _channel.Writer.TryWrite(watcher.Path);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }
}
