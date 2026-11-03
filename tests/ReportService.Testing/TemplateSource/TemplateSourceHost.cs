using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.TemplateSource;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// The template-source test double on a loopback port, over real HTTP: the
/// requests the client sends leave the process and come back, so the transport —
/// sockets, redirects, content length, raw blob bodies — is exercised and not
/// imagined. The double itself answers them, unchanged: this host only carries
/// its request log and its seeded packages to the wire.
/// </summary>
public sealed class TemplateSourceHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TemplateSourceHost(WebApplication app, Uri baseUrl, GitHubTemplateSourceStub stub)
    {
        _app = app;
        BaseUrl = baseUrl;
        Stub = stub;
    }

    /// <summary>The address the client is configured with.</summary>
    public Uri BaseUrl { get; }

    /// <summary>The double the requests reach.</summary>
    public GitHubTemplateSourceStub Stub { get; }

    /// <summary>Starts the host on a free loopback port.</summary>
    /// <param name="stub">The double to serve.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public static async Task<TemplateSourceHost> StartAsync(GitHubTemplateSourceStub stub, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stub);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(context => ForwardAsync(context, stub));
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var address = app.Urls.FirstOrDefault()
            ?? throw new InvalidOperationException("the template-source host did not bind an address");
        return new TemplateSourceHost(app, new Uri(address.EndsWith('/') ? address : address + "/", UriKind.Absolute), stub);
    }

    /// <summary>The settings a client reads this host with, as the take reads the configured source.</summary>
    public TemplateSourceOptions Options() => new()
    {
        ApiBaseUrl = BaseUrl.AbsoluteUri,
        Repository = GitHubTemplateSourceStub.Owner + "/" + GitHubTemplateSourceStub.Repository,
        Branch = GitHubTemplateSourceStub.Branch,
        AllowInsecureLoopback = true,
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);

    private static async Task ForwardAsync(HttpContext context, GitHubTemplateSourceStub stub)
    {
        var target = context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(target, UriKind.Relative));
        foreach (var header in context.Request.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                request.Content ??= new ByteArrayContent([]);
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        using var answer = await stub.AnswerAsync(request, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = (int)answer.StatusCode;
        foreach (var header in answer.Headers.Concat(answer.Content.Headers))
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        await answer.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

}
