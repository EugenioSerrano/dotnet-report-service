namespace ReportService.Templates;

/// <summary>
/// Which adapter composes a block, and why a block has none. The decision reads the engine
/// the taken package declares, cross-checks the pin the job was accepted with, and accepts
/// only an engine this deployment offers and a declared engine version whose major matches
/// the one the adapter runs. A refusal is a code and a reason, never a data value.
/// </summary>
/// <param name="Adapter">The adapter to compose with, or <see langword="null"/> when the block is refused.</param>
/// <param name="Code">The error code of a refusal: <c>TEMPLATE_INVALID</c> or <c>UNSUPPORTED_CAPABILITY</c>.</param>
/// <param name="Reason">The stable reason of a refusal.</param>
/// <param name="Message">One line naming the field and the engine names, and nothing else.</param>
public sealed record EngineDecision(
    ITemplateEngineAdapter? Adapter,
    string? Code = null,
    string? Reason = null,
    string? Message = null)
{
    /// <summary>Whether the block has an adapter to compose with.</summary>
    public bool Resolved => Adapter is not null;
}

/// <summary>
/// The one decision of which engine composes a block, taken before anything is composed: the
/// engine comes from the taken package's manifest, the pin is its cross-check, and the engine
/// must be one this deployment offers. All four refusals are non-retryable; the caller fails
/// the block with nothing composed, printed or written.
/// </summary>
public static class EngineResolution
{
    /// <summary>The code a package whose engine cannot be used answers with.</summary>
    public const string TemplateInvalid = "TEMPLATE_INVALID";

    /// <summary>The code an engine this deployment does not offer answers with.</summary>
    public const string UnsupportedCapability = TemplateErrorCodes.UnsupportedCapability;

    /// <summary>The reason a package declares no engine.</summary>
    public const string EngineUndeclared = "engine-undeclared";

    /// <summary>The reason the pin and the taken package disagree about the engine.</summary>
    public const string EngineMismatch = "engine-mismatch";

    /// <summary>The reason the deployment offers no adapter for the declared engine.</summary>
    public const string EngineUnsupported = "engine-unsupported";

    /// <summary>The reason the declared engine version's major differs from the adapter's.</summary>
    public const string EngineVersion = "engine-version";

    /// <summary>Decides which adapter composes <paramref name="manifest"/>'s package.</summary>
    /// <param name="manifest">The taken package's manifest.</param>
    /// <param name="pinEngine">The engine the job's pin recorded, as a manifest name, or <see langword="null"/> when the pin carries none.</param>
    /// <param name="registered">The adapters this deployment offers.</param>
    public static EngineDecision Resolve(RenderManifest manifest, string? pinEngine, TemplateEngineAdapterRegistry registered)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(registered);

        var declared = manifest.Engine;
        if (string.IsNullOrWhiteSpace(declared))
        {
            return new EngineDecision(
                null,
                TemplateInvalid,
                EngineUndeclared,
                $"{TemplateInvalid} reason={EngineUndeclared} field=engine");
        }

        if (!registered.TryGet(declared, out var adapter))
        {
            return new EngineDecision(
                null,
                UnsupportedCapability,
                EngineUnsupported,
                $"{UnsupportedCapability} reason={EngineUnsupported} field=engine declared={declared} offered={registered.OfferedNames}");
        }

        if (pinEngine is not null && !string.Equals(pinEngine, adapter.Engine, StringComparison.Ordinal))
        {
            return new EngineDecision(
                null,
                TemplateInvalid,
                EngineMismatch,
                $"{TemplateInvalid} reason={EngineMismatch} field=engine declared={declared} pinned={pinEngine}");
        }

        if (!string.IsNullOrWhiteSpace(manifest.EngineVersion))
        {
            var declaredMajor = EngineVersions.MajorOf(manifest.EngineVersion);
            var runsMajor = EngineVersions.MajorOf(adapter.Version);
            if (declaredMajor is not { } required || runsMajor != required)
            {
                return new EngineDecision(
                    null,
                    UnsupportedCapability,
                    EngineVersion,
                    $"{UnsupportedCapability} reason={EngineVersion} field=engineVersion declared={manifest.EngineVersion} offered={adapter.Version}");
            }
        }

        return new EngineDecision(adapter);
    }
}

/// <summary>
/// The one place the deployment says which engines it offers: the adapters the process
/// registered, by the engine name a manifest uses. An engine absent from this table is an
/// engine this deployment cannot compose, whatever a package declares.
/// </summary>
public sealed class TemplateEngineAdapterRegistry
{
    private readonly Dictionary<string, ITemplateEngineAdapter> adapters;

    /// <summary>Creates the registry of <paramref name="adapters"/>.</summary>
    /// <param name="adapters">Every adapter the process offers.</param>
    public TemplateEngineAdapterRegistry(IEnumerable<ITemplateEngineAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        this.adapters = new Dictionary<string, ITemplateEngineAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            if (!this.adapters.TryAdd(adapter.Engine, adapter))
            {
                throw new InvalidOperationException($"Two adapters register the engine '{adapter.Engine}'.");
            }
        }

        OfferedNames = this.adapters.Count == 0 ? "none" : string.Join(",", this.adapters.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>The engine names the deployment offers, ordinal, comma-separated, or <c>none</c>.</summary>
    public string OfferedNames { get; }

    /// <summary>The adapters the deployment offers, by engine name.</summary>
    public IReadOnlyCollection<ITemplateEngineAdapter> Adapters => adapters.Values;

    /// <summary>Finds the adapter for <paramref name="engine"/>.</summary>
    /// <param name="engine">The engine name a manifest uses.</param>
    /// <param name="adapter">The adapter, when the deployment offers the engine.</param>
    public bool TryGet(string? engine, out ITemplateEngineAdapter adapter)
    {
        if (engine is not null && adapters.TryGetValue(engine, out var found))
        {
            adapter = found;
            return true;
        }

        adapter = null!;
        return false;
    }
}
