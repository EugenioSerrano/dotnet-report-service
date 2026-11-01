using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dapper;
using DotNet.Testcontainers.Containers;
using Npgsql;
using ReportService.Migrations;
using ReportService.Testing;
using Temporalio.Client;
using Temporalio.Common;

namespace ReportService.IntegrationTests.Uploads;

/// <summary>
/// The transfer of a large body through the server the product actually ships: the
/// API and the orchestration worker started as separate processes, each limited to a
/// hundred and twenty-eight mebibytes of managed heap, over a real PostgreSQL, a real
/// workflow engine and a temporary storage root. Only the real server takes the
/// request-body limit the content operation lifts, which is why this bound is
/// measured here and nowhere else — in process the host refuses the body first.
/// <para>
/// The cases: a generated, non-seekable body of one gibibyte streams, is confirmed
/// and ends ready with the generator's hash, its run's peak working set staying
/// within thirty-two mebibytes of the same body cut to sixty-four mebibytes in fresh
/// processes; a chunked body beyond the size limit is stopped; and a body whose
/// client goes away inside its declared length is rejected at confirmation.
/// </para>
/// </summary>
public sealed class UploadTransferOverRealServerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    /// <summary>The most the one gibibyte run's peak working set may exceed the sixty-four mebibyte run's.</summary>
    public const long MemoryAllowance = 32L * 1024 * 1024;

    private const string ConsumerId = "dev-consumer";

    /// <summary>The heap limit both processes run under: one hundred and twenty-eight mebibytes.</summary>
    public const string HeapLimit = "0x8000000";

    private const int GrpcPort = 7233;

    private const int OneGibibyte = 1024 * 1024 * 1024;

    private const int SixtyFourMebibytes = 64 * 1024 * 1024;

    private static readonly TimeSpan EngineStartDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan ReadinessDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan VerificationDeadline = TimeSpan.FromMinutes(5);

    private static readonly string ApiDll = Path.Combine(
        RepositoryPaths.Root,
        "src",
        "ReportService.Api",
        "bin",
        "Release",
        "net10.0",
        "ReportService.Api.dll");

    private static readonly string WorkerDll = Path.Combine(
        RepositoryPaths.Root,
        "src",
        "ReportService.Workers.Orchestration",
        "bin",
        "Release",
        "net10.0",
        "ReportService.Workers.Orchestration.dll");

    private static readonly string ApiProject = Path.Combine(
        RepositoryPaths.Root,
        "src",
        "ReportService.Api",
        "ReportService.Api.csproj");

    private static readonly string WorkerProject = Path.Combine(
        RepositoryPaths.Root,
        "src",
        "ReportService.Workers.Orchestration",
        "ReportService.Workers.Orchestration.csproj");

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _image = CompositionPins.Load().WorkflowEngine;

    private readonly int _grpcPort = FreePort();

    private IContainer? _engine;

    private string? _database;

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public UploadTransferOverRealServerTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var migration = await MigrationRunner.RunAsync(
            _fixture.Application(_database),
            MigrationCatalog.Scripts,
            cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));

        await BuildAsync(ApiProject, cancellationToken);
        await BuildAsync(WorkerProject, cancellationToken);

        _engine = ContainerSmoke.Builder(_image, "temporal-transfer")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_grpcPort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _image, cancellationToken);
        await Wait.UntilAsync(
            $"Temporal gRPC ({_image.Reference}) on port {_grpcPort}",
            async token =>
            {
                var probe = await TemporalClient.ConnectAsync(
                    new TemporalClientConnectOptions($"127.0.0.1:{_grpcPort}") { Namespace = "default" });
                return await probe.Connection.CheckHealthAsync(options: new RpcOptions { CancellationToken = token });
            },
            EngineStartDeadline,
            TimeSpan.FromSeconds(1),
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task AOneGibibyteBodyStreamsAndVerifiesAndGrowsNoMoreThanA64MiBBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var small = await TransferAsync(SixtyFourMebibytes, cancellationToken);
        var large = await TransferAsync(OneGibibyte, cancellationToken);

        var figure = string.Create(
            CultureInfo.InvariantCulture,
            $"Transfer under a 128 MiB heap, fresh processes per body size — peak working set: "
            + $"64 MiB body API {small.ApiPeak / 1024.0 / 1024.0:F1} MiB, worker {small.WorkerPeak / 1024.0 / 1024.0:F1} MiB, "
            + $"{small.Elapsed.TotalSeconds:F1} s, peak temporary storage {small.PeakDisk / 1024.0 / 1024.0:F1} MiB; "
            + $"1 GiB body API {large.ApiPeak / 1024.0 / 1024.0:F1} MiB, worker {large.WorkerPeak / 1024.0 / 1024.0:F1} MiB, "
            + $"{large.Elapsed.TotalSeconds:F1} s, peak temporary storage {large.PeakDisk / 1024.0 / 1024.0:F1} MiB; "
            + $"bound: the 1 GiB run's peak is at most the 64 MiB run's + 32 MiB, for the API and for the worker.");
        TestContext.Current.TestOutputHelper?.WriteLine(figure);
        TestContext.Current.AddAttachment("transfer-memory-figure", figure);

        Assert.True(large.ApiPeak <= small.ApiPeak + MemoryAllowance, figure);
        Assert.True(large.WorkerPeak <= small.WorkerPeak + MemoryAllowance, figure);
    }

    [Fact]
    public async Task AChunkedBodyBeyondTheSizeLimitIsStoppedAndTheUploadEndsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const long quota = SixtyFourMebibytes;
        await using var servers = await StartAsync(quota, cancellationToken);
        var uploadId = await CreateAsync(servers, cancellationToken);

        var stopped = await PutAsync(servers, uploadId, quota * 2, cancellationToken);
        Assert.True(stopped, "the client has to see the transfer stopped: a refusal of the request or a closed connection.");

        var status = await WaitForEndedAsync(servers, uploadId, cancellationToken);
        Assert.Equal("rejected", status.GetProperty("status").GetString());
        Assert.Equal("QUOTA_EXCEEDED", status.GetProperty("rejectionCode").GetString());
        Assert.Equal("stored-bytes", status.GetProperty("rejectionReason").GetString());
        var received = status.GetProperty("receivedBytes").GetInt64();
        Assert.True(received <= quota + (64 * 1024), $"the transfer stopped at the limit, not past it: {received} bytes were received.");

        Assert.False(servers.Api.HasExited, "the API stays alive after a transfer stopped at the limit.");
        Assert.False(servers.Worker.HasExited, "the worker stays alive after a transfer stopped at the limit.");
    }

    [Fact]
    public async Task ABodyWhoseClientGoesAwayInsideItsDeclaredLengthIsRejectedAtConfirmation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var servers = await StartAsync(OneGibibyte, cancellationToken);
        var uploadId = await CreateAsync(servers, cancellationToken);

        var declared = 32 * 1024 * 1024;
        await SendHalfAndCloseAsync(servers, uploadId, declared, cancellationToken);

        var confirming = await servers.Client.PostAsync(
            new Uri($"/api/v1/uploads/{uploadId}/complete", UriKind.Relative),
            content: null,
            cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, confirming.StatusCode);

        var status = await WaitForEndedAsync(servers, uploadId, cancellationToken);
        Assert.Equal("rejected", status.GetProperty("status").GetString());
        Assert.Equal("DATA_INVALID", status.GetProperty("rejectionCode").GetString());
        Assert.Equal("truncated", status.GetProperty("rejectionReason").GetString());
        Assert.Equal(0L, await SnapshotRecordsAsync(uploadId, cancellationToken));
    }

    private async Task<Run> TransferAsync(int bodyBytes, CancellationToken cancellationToken)
    {
        await using var servers = await StartAsync(4L * 1024 * 1024 * 1024, cancellationToken);
        var elapsed = Stopwatch.StartNew();
        var uploadId = await CreateAsync(servers, cancellationToken);
        var expectedSha256 = GeneratedBodyStream.Sha256Of(bodyBytes);

        var stopped = await PutAsync(servers, uploadId, bodyBytes, cancellationToken);
        Assert.False(stopped, "the body of this case is inside the size limit and has to be taken whole.");

        var confirming = await servers.Client.PostAsync(
            new Uri($"/api/v1/uploads/{uploadId}/complete", UriKind.Relative),
            content: null,
            cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, confirming.StatusCode);

        var status = await WaitForReadyAsync(servers, uploadId, cancellationToken);
        elapsed.Stop();

        Assert.Equal("ready", status.GetProperty("status").GetString());
        Assert.Equal(expectedSha256, status.GetProperty("sha256").GetString());
        Assert.Equal(bodyBytes, status.GetProperty("sizeBytes").GetInt64());

        Assert.False(servers.Api.HasExited, "the API has to be alive at the end of the run.");
        Assert.False(servers.Worker.HasExited, "the worker has to be alive at the end of the run.");

        return new Run(servers.Api.PeakWorkingSet64, servers.Worker.PeakWorkingSet64, elapsed.Elapsed, servers.PeakDisk);
    }

    private async Task<ServerProcesses> StartAsync(long maxStoredBytes, CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "report-service-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);

        var apiUrl = $"http://127.0.0.1:{FreePort()}";
        var workerUrl = $"http://127.0.0.1:{FreePort()}";
        var connectionString = _fixture.Application(_database!);

        var shared = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_GCHeapHardLimit"] = HeapLimit,
            ["Storage__Provider"] = "filesystem",
            ["Storage__Filesystem__Root"] = root,
            ["ConnectionStrings__reportservice"] = connectionString,
            ["Database__ExpectedSchemaVersion"] = MigrationCatalog.ExpectedVersion,
            ["Uploads__MaxStoredBytes"] = maxStoredBytes.ToString(CultureInfo.InvariantCulture),
            ["Consumers__File"] = Path.Combine(RepositoryPaths.Root, "src", "ReportService.Api", "consumers.json"),
        };

        var workerEnvironment = new Dictionary<string, string>(shared, StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = workerUrl,
            ["Temporal__Address"] = $"127.0.0.1:{_grpcPort}",
            ["Temporal__Namespace"] = "default",
            ["Uploads__MaxUncompressedBytes"] = (4L * 1024 * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            ["Uploads__Verification__StartToClose"] = "00:10:00",
            ["Workers__DataActivitySlots"] = "2",
        };

        var apiEnvironment = new Dictionary<string, string>(shared, StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = apiUrl,
        };

        var api = Start(ApiDll, apiEnvironment);
        var worker = Start(WorkerDll, workerEnvironment);
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var servers = new ServerProcesses(api, worker, client, apiUrl, root);
        try
        {
            await Wait.UntilAsync(
                "the API answering /health",
                async token => await AnswersAsync(client, apiUrl, token),
                ReadinessDeadline,
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
            await Wait.UntilAsync(
                "the orchestration worker answering /health",
                async token => await AnswersAsync(client, workerUrl, token),
                ReadinessDeadline,
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
        }
        catch
        {
            await servers.DisposeAsync();
            throw;
        }

        return servers;
    }

    private static Process Start(string assembly, IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo("dotnet", $"\"{assembly}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (name, value) in environment)
        {
            info.Environment[name] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"the process {assembly} did not start.");
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<bool> AnswersAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(new Uri($"{url}/health"), cancellationToken);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task BuildAsync(string project, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("dotnet", $"build \"{project}\" -c Release --nologo -v quiet")
        {
            WorkingDirectory = RepositoryPaths.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var build = Process.Start(info) ?? throw new InvalidOperationException($"the build of {project} did not start.");
        var output = build.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = build.StandardError.ReadToEndAsync(cancellationToken);
        await build.WaitForExitAsync(cancellationToken);
        Assert.True(build.ExitCode == 0, $"the process {project} does not build: {await output} {await error}");
    }

    private static async Task<string> CreateAsync(ServerProcesses servers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/uploads", UriKind.Relative));
        request.Headers.Add("X-Consumer-Id", ConsumerId);
        request.Content = new StringContent(
            "{\"mode\":\"API_STREAM\",\"profile\":\"header-items-v1\"}",
            Encoding.UTF8,
            "application/json");
        using var response = await servers.Client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("uploadId").GetString()!;
    }

    // True when the client saw the transfer stopped: the refusal itself, or the connection going away.
    private static async Task<bool> PutAsync(
        ServerProcesses servers,
        string uploadId,
        long bodyBytes,
        CancellationToken cancellationToken)
    {
        using var body = new StreamContent(new GeneratedBodyStream(bodyBytes));
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"/api/v1/uploads/{uploadId}/content", UriKind.Relative))
        {
            Content = body,
        };
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Add("X-Consumer-Id", ConsumerId);

        try
        {
            using var response = await servers.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return false;
            }

            Assert.Equal(413, (int)response.StatusCode);
            return true;
        }
        catch (HttpRequestException)
        {
            // The server stopped reading and closed the connection; that is the stop.
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static async Task SendHalfAndCloseAsync(
        ServerProcesses servers,
        string uploadId,
        long declared,
        CancellationToken cancellationToken)
    {
        var address = new Uri(servers.Url);
        using var socket = new TcpClient();
        await socket.ConnectAsync(address.Host, address.Port, cancellationToken);
        socket.Client.LingerState = new LingerOption(true, 0);
        await using var stream = socket.GetStream();
        var headers = "PUT /api/v1/uploads/" + uploadId + "/content HTTP/1.1\r\n"
            + "Host: " + address.Host + ":" + address.Port.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + "X-Consumer-Id: " + ConsumerId + "\r\n"
            + "Content-Type: application/json\r\n"
            + "Content-Length: " + declared.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);

        var block = new byte[64 * 1024];
        for (var index = 0; index < block.Length; index++)
        {
            block[index] = (byte)(0x5A ^ (index & 0xFF));
        }

        var target = declared / 2;
        var sent = 0L;
        while (sent < target)
        {
            var take = (int)Math.Min(block.Length, target - sent);
            await stream.WriteAsync(block.AsMemory(0, take), cancellationToken);
            sent += take;
        }

        // The client leaves without sending the rest of what it declared.
        await stream.FlushAsync(cancellationToken);
    }

    private static Task<JsonElement> WaitForReadyAsync(
        ServerProcesses servers,
        string uploadId,
        CancellationToken cancellationToken) =>
        WaitForStatusAsync(
            servers,
            uploadId,
            "a ready upload",
            status => status.GetProperty("status").GetString() == "ready",
            VerificationDeadline,
            cancellationToken);

    private static Task<JsonElement> WaitForEndedAsync(
        ServerProcesses servers,
        string uploadId,
        CancellationToken cancellationToken) =>
        WaitForStatusAsync(
            servers,
            uploadId,
            "an upload that no longer accepts anything",
            status => status.GetProperty("status").GetString() is "ready" or "rejected",
            VerificationDeadline,
            cancellationToken);

    private static async Task<JsonElement> WaitForStatusAsync(
        ServerProcesses servers,
        string uploadId,
        string what,
        Func<JsonElement, bool> reached,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var found = JsonDocument.Parse("{}").RootElement.Clone();
        await Wait.UntilAsync(
            what,
            async token =>
            {
                using var response = await servers.Client.GetAsync(
                    new Uri($"/api/v1/uploads/{uploadId}", UriKind.Relative),
                    token);
                servers.Sample();
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                found = document.RootElement.Clone();
                return reached(found);
            },
            deadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return found;
    }

    private async Task<long> SnapshotRecordsAsync(string uploadId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_fixture.Application(_database!));
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.snapshots WHERE upload_id = @UploadId",
            new { UploadId = uploadId },
            cancellationToken: cancellationToken));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>What one body size cost: the two processes' peaks, the wall-clock and the storage the run held.</summary>
    private sealed record Run(long ApiPeak, long WorkerPeak, TimeSpan Elapsed, long PeakDisk);

    /// <summary>The API and the worker of one run, the client that talks to the API, and their temporary root.</summary>
    private sealed class ServerProcesses(
        Process api,
        Process worker,
        HttpClient client,
        string url,
        string root) : IAsyncDisposable
    {
        private long _peakDisk;

        public Process Api { get; } = api;

        public Process Worker { get; } = worker;

        public HttpClient Client { get; } = client;

        public string Url { get; } = url;

        public string Root { get; } = root;

        /// <summary>The most the storage root held while the run lasted, as sampled by the status poll.</summary>
        public long PeakDisk => Volatile.Read(ref _peakDisk);

        /// <summary>Records what the storage root holds now, keeping the largest figure seen.</summary>
        public void Sample()
        {
            var total = 0L;
            try
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (FileNotFoundException)
                    {
                        // A file the verification removed between the listing and the read.
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // The root is already gone.
            }

            Interlocked.Exchange(ref _peakDisk, Math.Max(Volatile.Read(ref _peakDisk), total));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            foreach (var process in new[] { Api, Worker })
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process had already ended.
                }

                process.Dispose();
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }
    }
}
