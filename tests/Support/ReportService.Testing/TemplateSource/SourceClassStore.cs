using System.Runtime.CompilerServices;
using ReportService.Storage;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// The composition's object store as the template source class sees it: an
/// outage of the source class can be switched on - every listing, open and info
/// of a <c>template-source</c> object answers a transient failure, while the
/// composition's own classes keep working - and every operation is recorded, so a
/// case can prove what the source did, and did not, read. Writes and deletes pass
/// through, because the composition stores the copies it takes and the artifacts
/// it renders through the same port.
/// </summary>
public sealed class SourceClassStore(IObjectStore inner) : IObjectStore
{
    /// <summary>The first segment of every key of the template source class.</summary>
    private const string ClassSegment = "template-source";

    private readonly List<SourceOperation> _operations = [];
    private readonly List<string> _opened = [];

    /// <summary>Whether the source class answers an outage; the composition's other classes never do.</summary>
    public bool Fail { get; set; }

    /// <summary>Every operation the store was asked for, in order.</summary>
    public IReadOnlyList<SourceOperation> Operations
    {
        get
        {
            lock (_operations)
            {
                return [.. _operations];
            }
        }
    }

    /// <summary>The operations that addressed the template source class, in order.</summary>
    public IReadOnlyList<SourceOperation> SourceOperations =>
        [.. Operations.Where(operation => IsOfTheClass(operation.Target))];

    /// <summary>Every key the store was asked to open, in order.</summary>
    public IReadOnlyList<string> Opened
    {
        get
        {
            lock (_opened)
            {
                return [.. _opened];
            }
        }
    }

    /// <summary>Forgets every operation recorded so far, so a case can observe one read only.</summary>
    public void Clear()
    {
        lock (_operations)
        {
            _operations.Clear();
        }

        lock (_opened)
        {
            _opened.Clear();
        }
    }

    /// <inheritdoc />
    public StorageCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken)
    {
        Record("write", key.ToString());
        return inner.WriteAsync(key, content, cancellationToken);
    }

    /// <inheritdoc />
    public Task<StorageResult<VerifiedRead>> OpenReadAsync(ObjectKey key, long expectedSize, string expectedSha256, CancellationToken cancellationToken)
    {
        var keyText = key.ToString();
        Record("open", keyText);
        lock (_opened)
        {
            _opened.Add(keyText);
        }

        return Fail && IsOfTheClass(keyText)
            ? Task.FromResult(StorageResult.Failure<VerifiedRead>(StorageOutcome.Transient, Failure()))
            : inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);
    }

    /// <inheritdoc />
    public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var keyText = key.ToString();
        Record("info", keyText);
        return Fail && IsOfTheClass(keyText)
            ? Task.FromResult(StorageResult.Failure<ObjectInfo>(StorageOutcome.Transient, Failure()))
            : inner.GetInfoAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        Record("delete", key.ToString());
        return inner.DeleteAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectInfo> ListAsync(
        string classPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Record("list", classPrefix);
        if (Fail && IsOfTheClass(classPrefix))
        {
            throw new StorageException(StorageOutcome.Transient, Failure());
        }

        await foreach (var item in inner.ListAsync(classPrefix, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private static bool IsOfTheClass(string target) =>
        string.Equals(target, ClassSegment, StringComparison.Ordinal)
        || target.StartsWith(ClassSegment + "/", StringComparison.Ordinal);

    private void Record(string verb, string target)
    {
        lock (_operations)
        {
            _operations.Add(new SourceOperation(verb, target));
        }
    }

    private static StorageError Failure() => new("storage.transient", "The object store did not answer.");

    /// <summary>One operation the store was asked for: what was asked of it, and where.</summary>
    /// <param name="Verb">The operation: <c>list</c>, <c>open</c>, <c>info</c>, <c>write</c> or <c>delete</c>.</param>
    /// <param name="Target">The prefix a listing was asked for, or the key another operation named.</param>
    public sealed record SourceOperation(string Verb, string Target)
    {
        /// <inheritdoc />
        public override string ToString() => Verb + " " + Target;
    }
}
