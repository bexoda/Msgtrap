using Msgtrap.Store;

namespace Msgtrap.Core;

/// <summary>
/// Deterministic fake credentials, one set per provider, so the UI can tell a
/// developer exactly what to put in their <c>.env</c>.
/// </summary>
/// <remarks>
/// Values are deliberately unrealistic (impossible account SIDs, obviously fake
/// secrets) so they can never be confused with production credentials.
/// </remarks>
public static class DefaultCredentials
{
    /// <summary>Arkesel's v2 key header value.</summary>
    public const string ArkeselApiKey = "msgtrap-dev-key";

    /// <summary>Hubtel client id / secret pair.</summary>
    public const string HubtelClientId = "msgtrap-hubtel-client";
    public const string HubtelClientSecret = "msgtrap-hubtel-secret";

    /// <summary>Twilio account SID (all zeroes) and auth token.</summary>
    public const string TwilioAccountSid = "AC00000000000000000000000000000000";
    public const string TwilioAuthToken = "msgtrap-twilio-token";

    /// <summary>mNotify API key.</summary>
    public const string MnotifyKey = "msgtrap-mnotify-key";

    /// <summary>Fresh copy of the defaults, keyed by provider id then field.</summary>
    public static Dictionary<string, Dictionary<string, string>> Create() => new()
    {
        ["arkesel"] = new() { ["api-key"] = ArkeselApiKey },
        ["hubtel"] = new()
        {
            ["client-id"] = HubtelClientId,
            ["client-secret"] = HubtelClientSecret,
        },
        ["twilio"] = new()
        {
            ["account-sid"] = TwilioAccountSid,
            ["auth-token"] = TwilioAuthToken,
        },
        ["mnotify"] = new() { ["key"] = MnotifyKey },
    };
}

/// <summary>
/// Reads the expected credentials for a provider and decides whether a request's
/// credentials are acceptable. Enforcement is a single global switch.
/// </summary>
public sealed class ProviderCredentials(MessageStore store)
{
    /// <summary>True when providers should reject mismatched credentials.</summary>
    public bool Enforced => store.Settings.EnforceCredentials;

    /// <summary>Expected value for one provider field, or an empty string.</summary>
    public string Value(string providerId, string field)
    {
        var all = store.Settings.Credentials;
        return all.TryGetValue(providerId, out var fields) && fields.TryGetValue(field, out var value)
            ? value
            : "";
    }

    /// <summary>All expected credential values for one provider (for the UI).</summary>
    public IReadOnlyDictionary<string, string> For(string providerId) =>
        store.Settings.Credentials.TryGetValue(providerId, out var fields)
            ? fields
            : new Dictionary<string, string>();

    /// <summary>
    /// True when the caller may proceed: either enforcement is off, or the
    /// supplied value matches the expected one.
    /// </summary>
    public bool Allows(string providerId, string field, string? supplied) =>
        !Enforced || string.Equals(supplied ?? "", Value(providerId, field), StringComparison.Ordinal);

    /// <summary>Same as <see cref="Allows"/> but for providers presenting several values at once.</summary>
    public bool AllowsAll(string providerId, params (string Field, string? Supplied)[] supplied) =>
        !Enforced || supplied.All(entry => Allows(providerId, entry.Field, entry.Supplied));
}
