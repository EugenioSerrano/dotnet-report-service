using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Corvus.Text.Json;
using Corvus.Text.Json.RuntimeEvaluator;
using CorvusJsonException = Corvus.Text.Json.JsonException;
using JsonDocument = System.Text.Json.JsonDocument;
using JsonElement = System.Text.Json.JsonElement;
using JsonValueKind = System.Text.Json.JsonValueKind;

namespace ReportService.DataContract;

/// <summary>
/// A template's data contract, compiled once: the schema document with the two roots
/// the service evaluates — the header and one record — sharing the one loaded document.
/// Evaluation runs over the reader's own bytes and answers whether the value conforms;
/// when it does not, it names the first violation's path and keyword and never a value
/// of the document. A value the contract cannot decide at all raises
/// <see cref="ContractEvaluationFailed"/>.
/// </summary>
public sealed class PreparedDataContract : IDisposable
{
    /// <summary>The root a header is evaluated at.</summary>
    public const string HeaderRoot = "#/properties/header";

    /// <summary>The root one record is evaluated at.</summary>
    public const string RecordRoot = "#/properties/items/items";

    private const int PathLimit = 1_024;
    private const int EllipsisBytes = 4;
    private const int MaximumResults = 64;

    private static readonly Regex DeclaredName = new(
        "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly JsonSchemaEvaluator _root;
    private readonly JsonSchemaEvaluator _header;
    private readonly JsonSchemaEvaluator _record;
    private readonly HashSet<string> _declaredNames;
    private bool _disposed;

    internal PreparedDataContract(
        JsonSchemaEvaluator root,
        JsonSchemaEvaluator header,
        JsonSchemaEvaluator record,
        HashSet<string> declaredNames,
        string packageSha256,
        string schemaPath)
    {
        _root = root;
        _header = header;
        _record = record;
        _declaredNames = declaredNames;
        PackageSha256 = packageSha256;
        SchemaPath = schemaPath;
    }

    /// <summary>The SHA-256 of the package the schema was taken from.</summary>
    public string PackageSha256 { get; }

    /// <summary>The path of the schema inside the package.</summary>
    public string SchemaPath { get; }

    /// <summary>Whether the contract has been disposed.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Evaluates a header at the header root.</summary>
    /// <param name="header">The header's own bytes, as the reader yielded them.</param>
    /// <returns>The verdict over the header.</returns>
    public ContractCheck CheckHeader(ReadOnlyMemory<byte> header) => Evaluate(_header, header);

    /// <summary>Evaluates one record at the record root.</summary>
    /// <param name="record">The record's own bytes, as the reader yielded them.</param>
    /// <returns>The verdict over the record.</returns>
    public ContractCheck CheckRecord(ReadOnlyMemory<byte> record) => Evaluate(_record, record);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _record.Dispose();
        _header.Dispose();
        _root.Dispose();
    }

    private ContractCheck Evaluate(JsonSchemaEvaluator root, ReadOnlyMemory<byte> value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            if (root.Evaluate(value, null))
            {
                return ContractCheck.Valid;
            }

            using var collector = JsonSchemaResultsCollector.Create(JsonSchemaResultsLevel.Basic, MaximumResults);
            root.Evaluate(value, collector);

            var first = FirstAssertion(collector);
            return first is null
                ? ContractCheck.Violation("$", "schema")
                : ContractCheck.Violation(
                    RenderPath(value, first.Value.InstanceLocation, first.Value.Keyword), first.Value.Keyword);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ContractEvaluationFailed(ContractEvaluationFailed.PatternTime, null);
        }
        catch (JsonSchemaEvaluationException)
        {
            throw new ContractEvaluationFailed(ContractEvaluationFailed.Depth, null);
        }
        catch (CorvusJsonException)
        {
            // The evaluator's own reader refuses a value nested past the contract's depth.
            throw new ContractEvaluationFailed(ContractEvaluationFailed.Depth, null);
        }
        catch (Exception exception) when (exception is not ContractEvaluationFailed)
        {
            throw new ContractEvaluationFailed(ContractEvaluationFailed.Evaluator, null, exception);
        }
    }

    private static (string InstanceLocation, string Keyword)? FirstAssertion(JsonSchemaResultsCollector collector)
    {
        foreach (var result in collector.EnumerateResults())
        {
            if (result.IsMatch)
            {
                continue;
            }

            var location = result.GetEvaluationLocationText();
            var keyword = LastSegment(location);
            if (keyword is not null && SchemaScan.AssertionKeywords.Contains(keyword))
            {
                return (result.GetDocumentEvaluationLocationText(), keyword);
            }
        }

        return null;
    }

    private static string? LastSegment(string pointer)
    {
        if (pointer.Length == 0)
        {
            return null;
        }

        var slash = pointer.LastIndexOf('/');
        return Unescape(pointer[(slash + 1)..]);
    }

    private string RenderPath(ReadOnlyMemory<byte> value, string instanceLocation, string keyword)
    {
        var segments = SplitPointer(instanceLocation);
        if (keyword is "additionalProperties" or "unevaluatedProperties" or "propertyNames"
            && segments.Count > 0)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        var rendered = new StringBuilder("$");
        var budget = PathLimit - EllipsisBytes;
        var length = 1;

        var element = RootElement(value);

        foreach (var segment in segments)
        {
            string text;
            if (element is { ValueKind: JsonValueKind.Array } array)
            {
                if (int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
                    && position < array.GetArrayLength())
                {
                    text = "[" + position.ToString(CultureInfo.InvariantCulture) + "]";
                    element = array[position];
                }
                else
                {
                    text = MemberSegment(segment);
                    element = null;
                }
            }
            else
            {
                text = MemberSegment(segment);
                element = element is { ValueKind: JsonValueKind.Object } container
                    && container.TryGetProperty(segment, out var child)
                    ? child
                    : null;
            }

            var size = Encoding.UTF8.GetByteCount(text);
            if (length + size > budget)
            {
                return rendered.Append(".…").ToString();
            }

            rendered.Append(text);
            length += size;
        }

        return rendered.ToString();
    }

    private static JsonElement? RootElement(ReadOnlyMemory<byte> value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private string MemberSegment(string name) =>
        _declaredNames.Contains(name)
            ? DeclaredName.IsMatch(name)
                ? "." + name
                : "['" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "']"
            : ".*";

    private static List<string> SplitPointer(string pointer)
    {
        var segments = new List<string>();
        if (pointer.Length == 0 || pointer == "/")
        {
            return segments;
        }

        foreach (var segment in pointer[1..].Split('/'))
        {
            segments.Add(Unescape(segment));
        }

        return segments;
    }

    private static string Unescape(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
