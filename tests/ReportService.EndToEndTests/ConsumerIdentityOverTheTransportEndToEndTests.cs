using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// The declared consumer identity over the API's real HTTP transport. A declaration carrying
/// a byte outside ASCII is answered by the service with the one refusal every unusable
/// identity receives - the same members, on every upload operation - because the identity is
/// a byte value the service judges, not one its transport refuses; the same bytes sent on
/// another request header stay refused by the transport. A managed client will not put such a
/// value on the wire, so every request of this class is written by hand on a socket.
/// </summary>
public sealed class ConsumerIdentityOverTheTransportEndToEndTests
{
    private const string RoutePrefix = "/api/v1/uploads";
    private const string CreateBody = """{"profile":"header-items-v1","mode":"API_STREAM"}""";
    private const string ContentBody = """{"items":[]}""";
    private const string AbsentUpload = "upload-44444444444444444444444444444444";
    private const string ProblemContentType = "application/problem+json";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    // dev-consum, the single byte 0xE9, r: the one-byte Latin-1 form of "é", as a caller that
    // is not UTF-8 sends it, and the two-byte UTF-8 form of the same character.
    private static readonly byte[] Latin1Identity =
        [.. Encoding.ASCII.GetBytes("dev-consum"), 0xE9, .. Encoding.ASCII.GetBytes("r")];

    private static readonly byte[] Utf8Identity =
        [.. Encoding.ASCII.GetBytes("dev-consum"), 0xC3, 0xA9, .. Encoding.ASCII.GetBytes("r")];

    private const string Latin1Text = "dev-consum\u00e9r";

    [Fact]
    public async Task AnIdentityOutsideAsciiIsAnsweredByTheServiceLikeEveryOtherUnusableOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await TestComposition.CreateAsync(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        }

        using var api = app.CreateHttpClient("api");
        var endpoint = api.BaseAddress!;
        Task<RawAnswer> Send(string method, string path, string? jsonBody, params (string Name, byte[] Value)[] headers) =>
            SendAsync(endpoint, method, path, jsonBody, headers, cancellationToken);

        // A creation request whose declaration carries the byte is answered with the service's
        // own refusal: the one problem document of an unusable identity, revealing nothing of
        // the value and creating no upload.
        var refused = await Send("POST", RoutePrefix, CreateBody, (ConsumerIdentityHeader, Latin1Identity));
        Assert.Equal(HttpStatusCode.Forbidden, (HttpStatusCode)refused.Status);
        var expected = ProblemOf(refused);
        using (var body = JsonDocument.Parse(refused.Body))
        {
            var root = body.RootElement;
            Assert.Equal("about:blank", root.GetProperty("type").GetString());
            Assert.Equal("Unknown consumer", root.GetProperty("title").GetString());
            Assert.Equal(403, root.GetProperty("status").GetInt32());
            Assert.Equal("The request does not carry a usable consumer identity.", root.GetProperty("detail").GetString());
            Assert.Equal("CONSUMER_UNKNOWN", root.GetProperty("code").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("traceId").GetString()));
            Assert.Equal("code,detail,status,title,traceId,type", string.Join(',', Members(root)));
            Assert.False(root.TryGetProperty("uploadId", out _), "a refused creation answered an upload identifier");
        }

        Assert.DoesNotContain("dev-consum", refused.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Latin1Text, refused.Text, StringComparison.Ordinal);

        // Every unusable declaration is one answer, whether the header is absent, empty, joined,
        // too long, space-bearing, simply unregistered or outside ASCII.
        var unusable = new List<RawAnswer>
        {
            await Send("POST", RoutePrefix, CreateBody),
            await Send("POST", RoutePrefix, CreateBody, (ConsumerIdentityHeader, Encoding.ASCII.GetBytes(string.Empty))),
            await Send("POST", RoutePrefix, CreateBody, (ConsumerIdentityHeader, Encoding.ASCII.GetBytes("first-consumer,second-consumer"))),
            await Send("POST", RoutePrefix, CreateBody, (ConsumerIdentityHeader, Encoding.ASCII.GetBytes(new string('a', 129)))),
            await Send("POST", RoutePrefix, CreateBody, (ConsumerIdentityHeader, Encoding.ASCII.GetBytes("dev consumer"))),
            await Send("POST", RoutePrefix, CreateBody, (ConsumerIdentityHeader, Encoding.ASCII.GetBytes("consumer-z"))),
            await Send("POST", RoutePrefix, CreateBody, (ConsumerIdentityHeader, Latin1Identity)),
        };

        foreach (var answer in unusable)
        {
            Assert.Equal(expected, ProblemOf(answer));
            Assert.Equal(HttpStatusCode.Forbidden, (HttpStatusCode)answer.Status);
        }

        // The same refusal holds on the other operations of an upload: the identity is decided
        // before any route logic, so the answer does not depend on the upload named.
        foreach (var operation in new (string Method, string Path, string? Body)[]
        {
            ("GET", $"{RoutePrefix}/{AbsentUpload}", null),
            ("PUT", $"{RoutePrefix}/{AbsentUpload}/content", ContentBody),
            ("POST", $"{RoutePrefix}/{AbsentUpload}/complete", null),
        })
        {
            var answer = await Send(operation.Method, operation.Path, operation.Body, (ConsumerIdentityHeader, Latin1Identity));

            Assert.Equal(expected, ProblemOf(answer));
        }

        // The header name is case-insensitive in the service's own reader, so the transport
        // selects the same reading for a lowercase spelling.
        var lowerCase = await Send("POST", RoutePrefix, CreateBody, ("x-consumer-id", Latin1Identity));
        Assert.Equal(expected, ProblemOf(lowerCase));

        // A declaration that is valid UTF-8 decodes to its characters as before and stays
        // refused by the identity's own characters, so nothing outside the grammar is widened.
        var utf8 = await Send("POST", RoutePrefix, CreateBody, (ConsumerIdentityHeader, Utf8Identity));
        Assert.Equal(expected, ProblemOf(utf8));

        // The socket path itself is sound: a registered identity on the same socket opens an
        // upload, and it is the only request of this class that yields an identifier.
        var created = await Send("POST", RoutePrefix, CreateBody, (ConsumerIdentityHeader, Encoding.ASCII.GetBytes("dev-consumer")));
        Assert.Equal(HttpStatusCode.Created, (HttpStatusCode)created.Status);
        using (var body = JsonDocument.Parse(created.Body))
        {
            var uploadId = body.RootElement.GetProperty("uploadId").GetString();
            Assert.False(string.IsNullOrEmpty(uploadId));
            Assert.StartsWith("upload-", uploadId, StringComparison.Ordinal);
        }

        // The allowance is the declared identity header's alone: the same bytes on another
        // request header are still refused by the transport, with no body and no code.
        var elsewhere = await Send("POST", RoutePrefix, CreateBody, ("X-Other-Header", Latin1Identity));
        Assert.Equal(HttpStatusCode.BadRequest, (HttpStatusCode)elsewhere.Status);
        Assert.Empty(elsewhere.Body);
    }

    private const string ConsumerIdentityHeader = "X-Consumer-Id";

    private static async Task<RawAnswer> SendAsync(
        Uri endpoint,
        string method,
        string path,
        string? jsonBody,
        (string Name, byte[] Value)[] headers,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Request(method, path, endpoint, jsonBody, headers), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        // Every request asks the connection to close, so the answer ends where the connection
        // does and is read whole before it is parsed.
        using var received = new MemoryStream();
        await stream.CopyToAsync(received, cancellationToken);
        return RawAnswer.Parse(received.ToArray());
    }

    private static byte[] Request(
        string method,
        string path,
        Uri endpoint,
        string? jsonBody,
        (string Name, byte[] Value)[] headers)
    {
        var bytes = new List<byte>();
        void Append(string text) => bytes.AddRange(Encoding.Latin1.GetBytes(text));

        Append($"{method} {path} HTTP/1.1\r\n");
        Append($"Host: {endpoint.Host}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}\r\n");
        Append("Connection: close\r\n");
        Append("Accept: application/json\r\n");
        if (jsonBody is not null)
        {
            Append("Content-Type: application/json\r\n");
            Append($"Content-Length: {Encoding.UTF8.GetByteCount(jsonBody).ToString(CultureInfo.InvariantCulture)}\r\n");
        }

        foreach (var (name, value) in headers)
        {
            Append($"{name}: ");
            bytes.AddRange(value);
            Append("\r\n");
        }

        Append("\r\n");
        if (jsonBody is not null)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(jsonBody));
        }

        return [.. bytes];
    }

    // The problem document of an answer, member for member, its trace identifier excluded.
    private static string ProblemOf(RawAnswer answer)
    {
        Assert.Equal(ProblemContentType, answer.Headers.GetValueOrDefault("Content-Type"));
        using var body = JsonDocument.Parse(answer.Body);
        return string.Join(
            ';',
            body.RootElement.EnumerateObject()
                .Where(member => member.Name != "traceId")
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .Select(member => member.Name + "=" + member.Value.GetRawText()));
    }

    private static List<string> Members(JsonElement root) =>
        [.. root.EnumerateObject().Select(member => member.Name).OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>One answer read off a socket: its status, its headers and its body.</summary>
    private sealed record RawAnswer(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        public string Text => Encoding.UTF8.GetString(Body);

        public static RawAnswer Parse(byte[] raw)
        {
            var headEnd = IndexOfHeaderEnd(raw);
            Assert.True(headEnd > 0, "the answer carried no complete header block");
            var head = Encoding.Latin1.GetString(raw, 0, headEnd);
            var lines = head.Split("\r\n");
            var status = int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            var body = raw[(headEnd + 4)..];
            return new RawAnswer(
                status,
                headers,
                headers.TryGetValue("Transfer-Encoding", out var encoding)
                    && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase)
                    ? Dechunk(body)
                    : body);
        }

        private static int IndexOfHeaderEnd(byte[] raw)
        {
            for (var index = 0; index + 3 < raw.Length; index++)
            {
                if (raw[index] == '\r' && raw[index + 1] == '\n' && raw[index + 2] == '\r' && raw[index + 3] == '\n')
                {
                    return index;
                }
            }

            return -1;
        }

        private static byte[] Dechunk(byte[] body)
        {
            using var content = new MemoryStream();
            var offset = 0;
            while (offset < body.Length)
            {
                var sizeEnd = body.AsSpan(offset).IndexOf("\r\n"u8);
                if (sizeEnd < 0)
                {
                    break;
                }

                var size = int.Parse(
                    Encoding.ASCII.GetString(body, offset, sizeEnd).Split(';')[0].Trim(),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture);
                offset += sizeEnd + 2;
                if (size == 0)
                {
                    break;
                }

                content.Write(body, offset, size);
                offset += size + 2;
            }

            return content.ToArray();
        }
    }
}
