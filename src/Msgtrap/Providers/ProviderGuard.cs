namespace Msgtrap.Providers;

/// <summary>Shared behaviour for provider routes: honouring the UI on/off switch.</summary>
public static class ProviderGuard
{
    /// <summary>True when the provider was switched off in the UI.</summary>
    public static bool IsDisabled(Store.MessageStore store, string providerId) =>
        store.Settings.DisabledProviders.Contains(providerId);

    /// <summary>
    /// 404 payload for a disabled provider. A disabled provider looks absent
    /// rather than broken, which is what you want when testing one provider only.
    /// </summary>
    public static IResult Disabled(string providerId) =>
        Results.Json(new
        {
            message = $"Provider '{providerId}' is disabled. Enable it in the Msgtrap Providers panel.",
        }, statusCode: StatusCodes.Status404NotFound);
}
