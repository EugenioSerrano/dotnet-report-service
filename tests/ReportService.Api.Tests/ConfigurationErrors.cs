using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests;

/// <summary>Finds the configuration error a host refused to start with, through the layers a test host wraps it in.</summary>
internal static class ConfigurationErrors
{
    public static ConfigurationError Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConfigurationError error)
            {
                return error;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            {
                return Find(aggregate.InnerExceptions[0]);
            }
        }

        throw new Xunit.Sdk.XunitException($"No configuration error was raised; got {exception?.GetType().FullName ?? "no exception"}.");
    }
}
