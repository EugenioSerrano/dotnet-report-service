using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReportService.Api.Uploads;
using ReportService.Jobs;
using ReportService.Uploads;

namespace ReportService.Api.Http;

/// <summary>
/// The one place an exception becomes an answer. A store that cannot be reached
/// is a retryable service condition (503); anything else is an unexpected fault
/// (500); both leave as a problem document built by <see cref="ApiProblem"/>, so
/// no environment ever answers a caller with a developer's exception page. The log
/// record keeps the exception's type — and its stack trace for the operator —
/// while the message stays out of it, because a message from a driver can carry a
/// connection detail.
/// </summary>
public static class ApiExceptionHandler
{
    /// <summary>The category of the API's own log records.</summary>
    public const string LogCategory = "ReportService.Api";

    /// <summary>The detail of every service-side answer.</summary>
    public const string ServiceUnavailableDetail = "The service could not complete the request. Retry it later.";

    /// <summary>The title of every service-side answer.</summary>
    public const string ServiceUnavailableTitle = "Service unavailable";

    /// <summary>Maps every unhandled exception to its problem document, in every environment.</summary>
    /// <param name="app">The application's pipeline.</param>
    public static IApplicationBuilder UseApiExceptionHandler(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = HandleAsync });
    }

    private static Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);

        if (exception is UploadStoreUnavailableException unavailable)
        {
            logger.LogWarning(
                "Upload store unavailable ({ExceptionType}, {SqlState})",
                unavailable.GetType().Name,
                unavailable.SqlState ?? "none");
            return Refuse(context, StatusCodes.Status503ServiceUnavailable);
        }

        // The job records are a store of the same class: a database that cannot be reached
        // or does not answer in time is the same retryable condition, never a fault.
        if (exception is JobStoreUnavailableException jobStore)
        {
            logger.LogWarning(
                "Job store unavailable ({ExceptionType}, {SqlState})",
                jobStore.GetType().Name,
                jobStore.SqlState ?? "none");
            return Refuse(context, StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogError(
            "Unhandled fault while serving a request ({ExceptionType}) {StackTrace}",
            exception?.GetType().Name ?? "none",
            exception?.StackTrace ?? string.Empty);
        return Refuse(context, StatusCodes.Status500InternalServerError);
    }

    private static Task Refuse(HttpContext context, int status) =>
        ApiProblem.Create(
            context,
            ErrorCodes.ServiceUnavailable,
            status,
            ServiceUnavailableTitle,
            ServiceUnavailableDetail).ExecuteAsync(context);
}
