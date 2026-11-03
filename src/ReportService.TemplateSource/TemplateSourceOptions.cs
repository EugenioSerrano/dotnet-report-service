using System.Text.RegularExpressions;
using ReportService.ServiceDefaults;

namespace ReportService.TemplateSource;

/// <summary>
/// The settings of the template source, in the <c>TemplateSource</c> section:
/// the one repository and branch every version is read from, the credential
/// that reads them, and the bounds one read must stay inside. The section is
/// required where it is used — a process that takes versions has no default
/// repository — and a problem names its setting and never its value, so a
/// planted token can never reach a log or a startup failure.
/// </summary>
public sealed partial class TemplateSourceOptions : IValidatedSettings
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionPath = "TemplateSource";

    /// <summary>The API version every request declares.</summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>The smallest request timeout, in seconds.</summary>
    public const int MinRequestTimeoutSeconds = 1;

    /// <summary>The largest request timeout, in seconds.</summary>
    public const int MaxRequestTimeoutSeconds = 60;

    /// <summary>The smallest read deadline, in seconds.</summary>
    public const int MinReadDeadlineSeconds = 5;

    /// <summary>The largest read deadline, in seconds.</summary>
    public const int MaxReadDeadlineSeconds = 120;

    /// <summary>The smallest resolution deadline, in seconds.</summary>
    public const int MinResolveDeadlineSeconds = 5;

    /// <summary>The largest resolution deadline, in seconds.</summary>
    public const int MaxResolveDeadlineSeconds = 300;

    /// <summary>The fewest files a package may hold.</summary>
    public const int MinFiles = 1;

    /// <summary>The most files a package may hold.</summary>
    public const int MaxFilesLimit = 256;

    /// <summary>The smallest file a package may hold, in bytes.</summary>
    public const int MinFileBytes = 1024;

    /// <summary>The largest file a package may hold, in bytes.</summary>
    public const int MaxFileBytesLimit = 8 * 1024 * 1024;

    /// <summary>The largest package a take accepts, in bytes.</summary>
    public const int MaxPackageBytesLimit = 32 * 1024 * 1024;

    /// <summary>Where the source's API answers; <c>https</c> unless it is a loopback the caller admitted.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    /// <summary>The repository, as <c>owner/name</c>; required.</summary>
    public string? Repository { get; set; }

    /// <summary>The branch versions are read from; required.</summary>
    public string? Branch { get; set; }

    /// <summary>The credential that reads the repository; never logged, traced or returned.</summary>
    public string? Token { get; set; }

    /// <summary>How long one request may take.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the whole read — commit, trees, blobs — may take.</summary>
    public TimeSpan ReadDeadline { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long one by-name resolution — the listing and every version it takes —
    /// may take, measured from the step that resolves it. It fits inside the step's
    /// own bound, and it can only shorten a take, never lengthen one.
    /// </summary>
    public TimeSpan ResolveDeadline { get; set; } = TimeSpan.FromSeconds(240);

    /// <summary>The most files a package may hold.</summary>
    public int MaxFiles { get; set; } = MaxFilesLimit;

    /// <summary>The largest file a package may hold, in bytes.</summary>
    public int MaxFileBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>The largest package a package may add up to, in bytes.</summary>
    public int MaxPackageBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Whether a plain-text loopback source is admitted; development and tests only.</summary>
    public bool AllowInsecureLoopback { get; set; }

    /// <summary>The owner part of <see cref="Repository"/>; empty when the setting is invalid.</summary>
    public string Owner => Repository is not null && Repository.Contains('/', StringComparison.Ordinal)
        ? Repository[..Repository.IndexOf('/', StringComparison.Ordinal)]
        : string.Empty;

    /// <summary>The name part of <see cref="Repository"/>; empty when the setting is invalid.</summary>
    public string RepositoryName => Repository is not null && Repository.Contains('/', StringComparison.Ordinal)
        ? Repository[(Repository.IndexOf('/', StringComparison.Ordinal) + 1)..]
        : string.Empty;

    /// <summary>True when a token is configured, without ever exposing it.</summary>
    public bool HasToken => !string.IsNullOrEmpty(Token);

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        foreach (var problem in ValidateApiBaseUrl())
        {
            yield return problem;
        }

        if (string.IsNullOrWhiteSpace(Repository))
        {
            yield return new ConfigurationProblem(nameof(Repository), "is required");
        }
        else if (!RepositoryPattern().IsMatch(Repository))
        {
            yield return new ConfigurationProblem(nameof(Repository), "is not 'owner/name' of ASCII letters, digits, '.', '_' and '-'");
        }

        if (string.IsNullOrWhiteSpace(Branch))
        {
            yield return new ConfigurationProblem(nameof(Branch), "is required");
        }
        else if (!BranchPattern().IsMatch(Branch)
            || Branch.Contains("..", StringComparison.Ordinal)
            || Branch[0] == '/'
            || Branch[^1] == '/')
        {
            yield return new ConfigurationProblem(nameof(Branch), "is not a branch name of ASCII letters, digits, '.', '_', '-' and '/' without '..' or a leading or trailing '/'");
        }

        if (RequestTimeout < TimeSpan.FromSeconds(MinRequestTimeoutSeconds) || RequestTimeout > TimeSpan.FromSeconds(MaxRequestTimeoutSeconds))
        {
            yield return new ConfigurationProblem(nameof(RequestTimeout), $"is not between {MinRequestTimeoutSeconds} and {MaxRequestTimeoutSeconds} seconds");
        }

        if (ReadDeadline < TimeSpan.FromSeconds(MinReadDeadlineSeconds) || ReadDeadline > TimeSpan.FromSeconds(MaxReadDeadlineSeconds))
        {
            yield return new ConfigurationProblem(nameof(ReadDeadline), $"is not between {MinReadDeadlineSeconds} and {MaxReadDeadlineSeconds} seconds");
        }

        if (ResolveDeadline < TimeSpan.FromSeconds(MinResolveDeadlineSeconds) || ResolveDeadline > TimeSpan.FromSeconds(MaxResolveDeadlineSeconds))
        {
            yield return new ConfigurationProblem(nameof(ResolveDeadline), $"is not between {MinResolveDeadlineSeconds} and {MaxResolveDeadlineSeconds} seconds");
        }

        if (MaxFiles < MinFiles || MaxFiles > MaxFilesLimit)
        {
            yield return new ConfigurationProblem(nameof(MaxFiles), $"is not between {MinFiles} and {MaxFilesLimit}");
        }

        if (MaxFileBytes < MinFileBytes || MaxFileBytes > MaxFileBytesLimit)
        {
            yield return new ConfigurationProblem(nameof(MaxFileBytes), $"is not between {MinFileBytes} and {MaxFileBytesLimit} bytes");
        }

        if (MaxPackageBytes <= 0 || MaxPackageBytes > MaxPackageBytesLimit)
        {
            yield return new ConfigurationProblem(nameof(MaxPackageBytes), $"is not between 1 and {MaxPackageBytesLimit} bytes");
        }
    }

    private IEnumerable<ConfigurationProblem> ValidateApiBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(ApiBaseUrl))
        {
            yield return new ConfigurationProblem(nameof(ApiBaseUrl), "is required");
            yield break;
        }

        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var baseUri))
        {
            yield return new ConfigurationProblem(nameof(ApiBaseUrl), "is not an absolute URI");
            yield break;
        }

        if (string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            yield break;
        }

        if (string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && baseUri.IsLoopback
            && AllowInsecureLoopback)
        {
            yield break;
        }

        yield return new ConfigurationProblem(nameof(ApiBaseUrl), "is not 'https'; a plain-text source is admitted only on loopback with AllowInsecureLoopback");
    }

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._/-]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex BranchPattern();
}
