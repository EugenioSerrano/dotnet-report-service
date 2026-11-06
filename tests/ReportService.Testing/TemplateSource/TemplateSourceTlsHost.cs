using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReportService.Testing.TemplateSource;

/// <summary>
/// The template-source double on a loopback port, over real TLS: the requests the
/// client sends leave the process, cross a socket and are answered by the double,
/// so the handshake, the chain and the transport are exercised and not imagined.
/// It binds <c>https://127.0.0.1:0</c> and reports the ephemeral port it was given;
/// it serves the certificate it is handed and the double it is given, exactly as
/// <see cref="TemplateSourceHost"/> does over plain HTTP. It is this suite's own
/// listener, never the end-to-end composition's double.
/// </summary>
public sealed class TemplateSourceTlsHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TemplateSourceTlsHost(WebApplication app, Uri baseUrl, GitHubTemplateSourceStub stub)
    {
        _app = app;
        BaseUrl = baseUrl;
        Stub = stub;
    }

    /// <summary>The address the client is configured with.</summary>
    public Uri BaseUrl { get; }

    /// <summary>The double the requests reach.</summary>
    public GitHubTemplateSourceStub Stub { get; }

    /// <summary>Starts the host on a free loopback port, serving <paramref name="certificate"/>.</summary>
    /// <param name="stub">The double to serve.</param>
    /// <param name="certificate">The certificate the listener presents, with its key.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    public static async Task<TemplateSourceTlsHost> StartAsync(
        GitHubTemplateSourceStub stub,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stub);
        ArgumentNullException.ThrowIfNull(certificate);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(
            IPAddress.Loopback,
            0,
            listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.Run(context => ForwardAsync(context, stub));
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var address = Addresses(app).FirstOrDefault()
            ?? throw new InvalidOperationException("the template-source host did not bind an address");
        return new TemplateSourceTlsHost(app, new Uri(address.EndsWith('/') ? address : address + "/", UriKind.Absolute), stub);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);

    // The address the server actually bound: the port it reports, never the one asked for.
    private static IEnumerable<string> Addresses(WebApplication app)
    {
        var reported = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        return reported is { Count: > 0 } ? reported : app.Urls;
    }

    private static async Task ForwardAsync(HttpContext context, GitHubTemplateSourceStub stub)
    {
        // The double reads the request as the client sent it: an absolute address, whose path
        // and query are what it routes on.
        var target = string.Concat(
            context.Request.Scheme,
            "://",
            context.Request.Host.ToUriComponent(),
            context.Request.Path.ToUriComponent(),
            context.Request.QueryString.ToUriComponent());
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(target, UriKind.Absolute));
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
