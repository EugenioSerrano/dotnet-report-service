using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReportService.Storage;

namespace ReportService.ServiceDefaults;

/// <summary>
/// Two development-only endpoints that write and read an object through the
/// process's store, so the development composition can show that its
/// processes share one storage root. They are mounted only when
/// <c>Storage:Diagnostics:Enabled</c> is true — the development composition
/// sets it, a deployed profile does not — and are not a product API:
/// <list type="bullet">
/// <item><c>POST /diagnostics/storage/{key}</c> writes the request body once and returns <c>{ key, size, sha256 }</c>;</item>
/// <item><c>GET /diagnostics/storage/{key}?size=&amp;sha256=</c> reads the object verified against those values and returns <c>{ key, size, sha256, verified }</c>.</item>
/// </list>
/// A refused key is <c>400</c> with the grammar's reason, a missing object
/// <c>404</c>, an existing key <c>409</c>, a content that does not match
/// <c>422</c>; an error body never repeats the key.
/// </summary>
public static class StorageDiagnostics
{
    /// <summary>The path under which the endpoints are mounted.</summary>
    public const string RoutePrefix = "/diagnostics/storage";

    /// <summary>Mounts the endpoints when <c>Storage:Diagnostics:Enabled</c> is true; otherwise does nothing.</summary>
    public static IEndpointRouteBuilder MapStorageDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<ObjectStorageSettings>>().Value;
        if (!settings.Diagnostics.Enabled)
        {
            return endpoints;
        }

        RequestDelegate write = WriteAsync;
        RequestDelegate read = ReadAsync;
        endpoints.MapPost(RoutePrefix + "/{**key}", write);
        endpoints.MapGet(RoutePrefix + "/{**key}", read);
        return endpoints;
    }

    private static async Task WriteAsync(HttpContext context)
    {
        if (!TryReadKey(context, out var key, out var reason))
        {
            await RespondAsync(context, StatusCodes.Status400BadRequest, new { code = "storage.invalid-key", reason }).ConfigureAwait(false);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IObjectStore>();
        var result = await store.WriteAsync(key, context.Request.Body, context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await RespondFailureAsync(context, result.Outcome, result.Error!.Code).ConfigureAwait(false);
            return;
        }

        await RespondAsync(context, StatusCodes.Status200OK, new { key = key.ToString(), size = result.Value!.Size, sha256 = result.Value.Sha256 }).ConfigureAwait(false);
    }

    private static async Task ReadAsync(HttpContext context)
    {
        if (!TryReadKey(context, out var key, out var reason))
        {
            await RespondAsync(context, StatusCodes.Status400BadRequest, new { code = "storage.invalid-key", reason }).ConfigureAwait(false);
            return;
        }

        var query = context.Request.Query;
        if (!long.TryParse(query["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            || query["sha256"].ToString() is not { } sha256
            || !Sha256Text.IsValid(sha256))
        {
            await RespondAsync(context, StatusCodes.Status400BadRequest, new
            {
                code = "storage.invalid-expectation",
                reason = "size must be a non-negative integer and sha256 64 lower-case hexadecimal characters",
            }).ConfigureAwait(false);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IObjectStore>();
        var result = await store.OpenReadAsync(key, size, sha256, context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await RespondFailureAsync(context, result.Outcome, result.Error!.Code).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var verified = result.Value!;
            await verified.Content.CopyToAsync(Stream.Null, context.RequestAborted).ConfigureAwait(false);
        }
        catch (StorageIntegrityException exception)
        {
            await RespondFailureAsync(context, exception.Outcome, exception.Error.Code).ConfigureAwait(false);
            return;
        }

        await RespondAsync(context, StatusCodes.Status200OK, new { key = key.ToString(), size, sha256, verified = true }).ConfigureAwait(false);
    }

    private static bool TryReadKey(HttpContext context, out ObjectKey key, out string reason) =>
        ObjectKey.TryCreate(context.Request.RouteValues["key"] as string, out key, out reason);

    private static Task RespondFailureAsync(HttpContext context, StorageOutcome outcome, string code) =>
        RespondAsync(context, outcome switch
        {
            StorageOutcome.NotFound => StatusCodes.Status404NotFound,
            StorageOutcome.AlreadyExists => StatusCodes.Status409Conflict,
            StorageOutcome.IntegrityFailure => StatusCodes.Status422UnprocessableEntity,
            StorageOutcome.Transient => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        }, new { outcome = outcome.ToString(), code });

    private static Task RespondAsync<T>(HttpContext context, int status, T body)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}
