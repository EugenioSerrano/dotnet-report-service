using Microsoft.Extensions.Configuration;

namespace ReportService.Api.Tests;

/// <summary>
/// An in-memory configuration whose whole content can be replaced, raising the
/// configuration's change notification synchronously on the calling thread, the
/// way a watched file does after it is rewritten.
/// </summary>
internal sealed class ReplaceableConfiguration
{
    private readonly Provider _provider;

    public ReplaceableConfiguration(IReadOnlyDictionary<string, string?> initial)
    {
        var source = new Source(initial);
        Root = new ConfigurationBuilder().Add(source).Build();
        _provider = source.Created!;
    }

    public IConfigurationRoot Root { get; }

    /// <summary>Replaces every value and notifies the change.</summary>
    public void Replace(IReadOnlyDictionary<string, string?> values) => _provider.Replace(values);

    private sealed class Source(IReadOnlyDictionary<string, string?> initial) : IConfigurationSource
    {
        public Provider? Created { get; private set; }

        public IConfigurationProvider Build(IConfigurationBuilder builder) => Created = new Provider(initial);
    }

    private sealed class Provider : ConfigurationProvider
    {
        public Provider(IReadOnlyDictionary<string, string?> initial) => Set(initial);

        public void Replace(IReadOnlyDictionary<string, string?> values)
        {
            Set(values);
            OnReload();
        }

        private void Set(IReadOnlyDictionary<string, string?> values) =>
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
    }
}
