using Msgtrap.Arkesel;

namespace Msgtrap.Providers;

/// <summary>
/// Arkesel adapter: <c>/api/v2/sms/send</c>, <c>/api/v2/clients/balance-details</c>
/// and the OTP endpoints.
/// </summary>
/// <remarks>
/// The implementation lives in <c>Arkesel/</c> because Arkesel is the provider
/// Msgtrap was built for; this class only publishes its metadata and routes.
/// </remarks>
public sealed class ArkeselProvider(
    ArkeselEndpoints endpoints,
    OtpEndpoints otp) : ISmsProvider
{
    public string Id => ArkeselEndpoints.ProviderId;
    public string DisplayName => "Arkesel";
    public string Summary => "Arkesel SMS API v2 plus the OTP endpoints";
    public string DocsUrl => "https://developers.arkesel.com/";

    public IReadOnlyList<ProviderEndpoint> Endpoints { get; } =
    [
        new("POST", "/api/v2/sms/send", "Send, schedule, sandbox or request a callback"),
        new("GET", "/api/v2/clients/balance-details", "Fake SMS and wallet balance"),
        new("POST", "/api/otp/generate", "Generate and capture a one-time code"),
        new("POST", "/api/otp/verify", "Verify a code, with expiry and single use"),
    ];

    public IReadOnlyList<CredentialField> Credentials { get; } =
    [
        new("api-key", "API key", "api-key header"),
    ];

    public void Map(IEndpointRouteBuilder app)
    {
        endpoints.Map(app);
        otp.Map(app);
    }
}
