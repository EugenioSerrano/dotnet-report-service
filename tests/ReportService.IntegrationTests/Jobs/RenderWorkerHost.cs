extern alias pdf;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The render worker of one class's composition: the product's own PDF process, on the same
/// engine, metadata database and storage root the class's other hosts use, reading the
/// deployment's template source - the composition's own root, through the source class. The
/// process declares the queue the printing path schedules its render on, so the work a case
/// drives is served instead of waited on, exactly as <see cref="PrintingRig"/> and
/// <c>JobTemplateResolutionTests</c> already compose it.
/// <para>
/// The source class is published into that root before the worker starts, with the sample
/// package of the one template the class prints: the source serves what the class's jobs may
/// ask for and nothing else. It lives as long as the worker and is disposed after it.
/// </para>
/// </summary>
internal sealed class RenderWorkerHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<PdfWorker> _worker;

    private readonly TemplateSourceClass _source;

    private RenderWorkerHost(WebApplicationFactory<PdfWorker> worker, TemplateSourceClass source)
    {
        _worker = worker;
        _source = source;
    }

    /// <summary>
    /// Publishes the class's sample package into the composition's storage root, starts the
    /// render worker on that composition, waits for the worker's health check and answers the
    /// host to dispose.
    /// </summary>
    /// <param name="enginePort">The port the class's workflow engine listens on.</param>
    /// <param name="connectionString">The class's metadata database.</param>
    /// <param name="storageRoot">The storage root the class's other hosts write to.</param>
    /// <param name="templateId">The template the class's jobs print.</param>
    /// <param name="readyDeadline">The class's own readiness bound.</param>
    /// <param name="logs">The class's log capture, when it has one; the third process logs there too.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async ValueTask<RenderWorkerHost> StartAsync(
        int enginePort,
        string connectionString,
        string storageRoot,
        string templateId,
        TimeSpan readyDeadline,
        LogCapture? logs,
        CancellationToken cancellationToken)
    {
        var (version, files) = SamplePackage(templateId);

        // The source's own root, beside the storage root the class's other hosts write to:
        // the worker reads the class a publisher creates, and this fixture is that publisher.
        var sourceRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storageRoot))!, "template-source");
        var source = await TemplateSourceClass.CreateAsync(sourceRoot);
        await source.PublishAsync(templateId, version, files);

        WebApplicationFactory<PdfWorker>? worker = null;
        try
        {
            worker = Worker(enginePort, connectionString, storageRoot, sourceRoot, logs);
            using var client = worker.CreateClient();
            await Wait.UntilAsync(
                "the render worker answering its health check",
                async token =>
                {
                    using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                    return response.StatusCode == HttpStatusCode.OK;
                },
                readyDeadline,
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }
        catch
        {
            if (worker is not null)
            {
                await worker.DisposeAsync();
            }

            await source.DisposeAsync();
            throw;
        }

        return new RenderWorkerHost(worker, source);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // The worker is the only process that reads the source, so it stops first.
        await _worker.DisposeAsync();
        await _source.DisposeAsync();
    }

    // The render worker on the product's own registrations, with the settings the other
    // compositions of this level already give it.
    private static WebApplicationFactory<PdfWorker> Worker(
        int enginePort,
        string connectionString,
        string storageRoot,
        string sourceRoot,
        LogCapture? logs) =>
        new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{enginePort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", storageRoot);
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting("TemplateSource:Store:Filesystem:Root", sourceRoot);
            builder.UseSetting("TemplateSource:Store:Filesystem:CreateIfMissing", "false");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            if (logs is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            }
        });

    // The sample template's own files, the sample document included, under the version its
    // manifest declares.
    private static (string Version, IReadOnlyDictionary<string, byte[]> Files) SamplePackage(string templateId)
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "templates", templateId);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            files[relative] = File.ReadAllBytes(file);
        }

        var read = new TemplateManifestReader()
            .Read(new MemoryStream(files[TemplateManifestReader.FileName], writable: false));
        if (!read.Succeeded || read.Manifest?.Version is not { Length: > 0 } version)
        {
            throw new InvalidOperationException(
                $"the sample template '{templateId}' carries no manifest a version can be taken from: {read.Reason}");
        }

        return (version, files);
    }
}
