using Msgtrap.Store;

namespace Msgtrap.Providers;

/// <summary>What the UI needs to describe one provider.</summary>
/// <param name="Id">Adapter id.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="Summary">Which API is emulated.</param>
/// <param name="DocsUrl">Provider documentation link.</param>
/// <param name="Endpoints">Routes the adapter serves.</param>
/// <param name="Credentials">Expected credentials, with the fake values to use.</param>
/// <param name="Enabled">False when the provider is disabled at runtime from the UI.</param>
public sealed record ProviderInfo(
    string Id,
    string DisplayName,
    string Summary,
    string DocsUrl,
    IReadOnlyList<ProviderEndpoint> Endpoints,
    IReadOnlyDictionary<string, string> Credentials,
    bool Enabled);

/// <summary>
/// Holds the providers loaded at startup and answers whether a request may be
/// served, based on the <c>--provider</c> restriction and the runtime toggle.
/// </summary>
/// <remarks>
/// Startup wins: a provider excluded with <c>--provider</c> is never mapped, so
/// it cannot be re-enabled from the UI. Within the loaded set, the UI can turn
/// providers on and off without a restart.
/// </remarks>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ISmsProvider> _providers;

    public ProviderRegistry(IEnumerable<ISmsProvider> providers, AppOptions options)
    {
        var requested = options.Providers;
        var all = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);

        // Unknown ids in --provider are surfaced immediately rather than silently ignored.
        var unknown = requested.Where(id => !all.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown provider(s): {string.Join(", ", unknown)}. Available: {string.Join(", ", all.Keys.Order())}");
        }

        _providers = requested.Length == 0
            ? all
            : requested.ToDictionary(id => id, id => all[id], StringComparer.OrdinalIgnoreCase);

        StartupRestricted = requested.Length > 0;
    }

    /// <summary>True when <c>--provider</c> limited the loaded set.</summary>
    public bool StartupRestricted { get; }

    /// <summary>Providers loaded at startup, in registration order.</summary>
    public IReadOnlyCollection<ISmsProvider> Loaded => _providers.Values;

    /// <summary>Looks up a loaded provider.</summary>
    public ISmsProvider? Find(string id) => _providers.GetValueOrDefault(id);

    /// <summary>
    /// True when the provider is loaded and enabled in settings. Providers serve
    /// 404 when this is false, so a disabled provider looks absent rather than broken.
    /// </summary>
    public bool IsServing(string id, MessageStore store) =>
        _providers.ContainsKey(id) && !store.Settings.DisabledProviders.Contains(id);

    /// <summary>Builds the UI payload for every loaded provider.</summary>
    public IReadOnlyList<ProviderInfo> Describe(MessageStore store, Core.ProviderCredentials credentials)
    {
        var disabled = store.Settings.DisabledProviders;
        return _providers.Values
            .Select(provider => new ProviderInfo(
                provider.Id,
                provider.DisplayName,
                provider.Summary,
                provider.DocsUrl,
                provider.Endpoints,
                credentials.For(provider.Id),
                !disabled.Contains(provider.Id)))
            .ToList();
    }
}
