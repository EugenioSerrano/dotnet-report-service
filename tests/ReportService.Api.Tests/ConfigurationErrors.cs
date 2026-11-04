using System.Text;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests;

/// <summary>
/// Finds the configuration error a host refused to start with, through the layers a test
/// host wraps it in. When the chain carries none, the failure names the exception it got
/// and reports the chain whole — every exception's type, message, inner exceptions and
/// frames — because the type alone is what left a lost start error unreadable.
/// </summary>
internal static class ConfigurationErrors
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    public static ConfigurationError Find(Exception? exception)
    {
        foreach (var current in Flatten(exception))
        {
            if (current is ConfigurationError error)
            {
                return error;
            }
        }

        throw new Xunit.Sdk.XunitException(Describe(exception));
    }

    /// <summary>Every exception of the chain, outermost first, following inner exceptions and every inner of an aggregate.</summary>
    private static IEnumerable<Exception> Flatten(Exception? exception)
    {
        if (exception is null)
        {
            yield break;
        }

        yield return exception;
        foreach (var inner in Inners(exception))
        {
            foreach (var nested in Flatten(inner))
            {
                yield return nested;
            }
        }
    }

    private static string Describe(Exception? exception) =>
        exception is null
            ? "No configuration error was raised; got no exception. The start raised nothing, so the chain reported here is empty."
            : $"No configuration error was raised; got {exception.GetType().FullName}.{Environment.NewLine}"
                + $"The chain of exceptions the start raised, outermost first:{Environment.NewLine}{Render(exception, 0)}";

    private static string Render(Exception exception, int level)
    {
        var indent = new string(' ', level * 2);
        var text = new StringBuilder();
        var message = exception.Message.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        text.Append(indent).Append(exception.GetType().FullName).Append(": ").AppendLine(message.FirstOrDefault() ?? string.Empty);
        foreach (var continuation in message.Skip(1))
        {
            text.Append(indent).Append("   ").AppendLine(continuation);
        }

        if (exception.StackTrace is { } stack)
        {
            foreach (var line in stack.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                text.Append(indent).Append("   ").AppendLine(line);
            }
        }
        else
        {
            text.Append(indent).AppendLine("   no stack trace was recorded");
        }

        foreach (var inner in Inners(exception))
        {
            text.Append(Render(inner, level + 1));
        }

        return text.ToString();
    }

    private static IEnumerable<Exception> Inners(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions;
        }

        return exception.InnerException is { } inner ? new[] { inner } : Array.Empty<Exception>();
    }
}
