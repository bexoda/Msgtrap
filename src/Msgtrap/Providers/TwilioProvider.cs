using System.Text.Json;
using Msgtrap.Core;
using Msgtrap.Store;

namespace Msgtrap.Providers;

/// <summary>
/// Twilio adapter: <c>POST /2010-04-01/Accounts/{AccountSid}/Messages.json</c>.
/// </summary>
/// <remarks>
/// Twilio authenticates with HTTP Basic (AccountSid as username, auth token as
/// password) and takes form-encoded <c>To</c>, <c>From</c>, <c>Body</c> and
/// <c>StatusCallback</c>. Message creation returns <c>201 Created</c> with a
/// <c>Message</c> resource. JSON bodies are accepted too, purely for convenience
/// while testing by hand. Error codes are Twilio's published ones.
/// </remarks>
public sealed class TwilioProvider(
    MessageStore store,
    MessageIngest ingest,
    ProviderCredentials credentials,
    HttpClient http,
    ILogger<TwilioProvider> logger) : ISmsProvider
{
    /// <summary>Provider id.</summary>
    public const string ProviderId = "twilio";

    // Twilio error codes used by this adapter.
    private const int ErrorAuthFailed = 20003;
    private const int ErrorToRequired = 21604;
    private const int ErrorToInvalid = 21211;
    private const int ErrorBodyRequired = 21602;

    public string Id => ProviderId;
    public string DisplayName => "Twilio";
    public string Summary => "Twilio Programmable Messaging (Messages resource)";
    public string DocsUrl => "https://www.twilio.com/docs/messaging/api/message-resource";

    public IReadOnlyList<ProviderEndpoint> Endpoints { get; } =
    [
        new("POST", "/2010-04-01/Accounts/{AccountSid}/Messages.json", "Create a message (form-encoded)"),
    ];

    public IReadOnlyList<CredentialField> Credentials { get; } =
    [
        new("account-sid", "Account SID", "basic auth user and URL path"),
        new("auth-token", "Auth token", "basic auth password"),
    ];

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/2010-04-01/Accounts/{accountSid}/Messages.json",
            (HttpContext context, string accountSid, CancellationToken cancellationToken) =>
                SendAsync(context, accountSid, cancellationToken));
    }

    private async Task<IResult> SendAsync(HttpContext context, string accountSid, CancellationToken cancellationToken)
    {
        if (ProviderGuard.IsDisabled(store, ProviderId)) return ProviderGuard.Disabled(ProviderId);

        var fields = await RequestFields.QueryOrFormAsync(context, cancellationToken);
        var basic = context.BasicAuth();
        var suppliedSid = basic?.User ?? fields.Pick("AccountSid", "accountSid");

        if (!credentials.AllowsAll(ProviderId,
                ("account-sid", suppliedSid),
                ("auth-token", basic?.Password)))
        {
            logger.LogWarning("Rejected Twilio send: authentication {State}",
                suppliedSid is null ? "missing" : "does not match");
            return TwilioError(StatusCodes.Status401Unauthorized, ErrorAuthFailed,
                "Authentication Error - invalid username");
        }

        // The account SID appears in the path as well; a mismatch is a 404 on Twilio.
        if (credentials.Enforced && !string.Equals(accountSid, credentials.Value(ProviderId, "account-sid"), StringComparison.Ordinal))
            return TwilioError(StatusCodes.Status404NotFound, 20404, "The requested resource was not found");

        var to = fields.Pick("To", "to");
        var from = fields.Pick("From", "from");
        var body = fields.Pick("Body", "body");
        var statusCallback = fields.Pick("StatusCallback", "statusCallback");

        if (context.Request.HasJsonContentType())
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                to = JsonField(document.RootElement, "To", "to") ?? to;
                from = JsonField(document.RootElement, "From", "from") ?? from;
                body = JsonField(document.RootElement, "Body", "body") ?? body;
                statusCallback = JsonField(document.RootElement, "StatusCallback", "statusCallback") ?? statusCallback;
            }
        }

        if (string.IsNullOrWhiteSpace(to))
            return TwilioError(StatusCodes.Status400BadRequest, ErrorToRequired, "A 'To' phone number is required.");
        if (string.IsNullOrWhiteSpace(body))
            return TwilioError(StatusCodes.Status400BadRequest, ErrorBodyRequired, "Message body is required.");

        var result = ingest.Capture(new InboundSms(
            Provider: ProviderId,
            Sender: from ?? "",
            Body: body,
            Recipients: [to],
            CallbackUrl: statusCallback,
            Raw: fields.ToDictionary(entry => entry.Key, entry => (object?)entry.Value),
            Notify: NotifyAsync));

        if (result.Accepted.Count == 0)
        {
            return TwilioError(StatusCodes.Status400BadRequest, ErrorToInvalid,
                $"The 'To' number {to} is not a valid phone number.");
        }

        var recipient = result.Accepted[0];
        var sid = "SM" + recipient.Id.Replace("-", "");
        var timestamp = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'");

        // 201 Created is what Twilio returns for a new Message resource.
        return Results.Json(new Dictionary<string, object?>
        {
            ["sid"] = sid,
            ["account_sid"] = credentials.Value(ProviderId, "account-sid"),
            ["to"] = E164(to),
            ["from"] = from ?? "",
            ["body"] = body,
            ["status"] = "queued",
            ["num_segments"] = result.Message.Segments.ToString(),
            ["direction"] = "outbound-api",
            ["api_version"] = "2010-04-01",
            ["date_created"] = timestamp,
            ["date_updated"] = timestamp,
            ["uri"] = $"/2010-04-01/Accounts/{credentials.Value(ProviderId, "account-sid")}/Messages/{sid}.json",
            ["price"] = null,
            ["error_code"] = null,
            ["error_message"] = null,
            ["messaging_service_sid"] = null,
        }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>Twilio status callback: form POST with MessageSid and MessageStatus.</summary>
    private Task NotifyAsync(string callbackUrl, Recipient recipient)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("Ignoring Twilio StatusCallback {Url}: only absolute http(s) URLs are supported", callbackUrl);
            return Task.CompletedTask;
        }

        var sid = "SM" + recipient.Id.Replace("-", "");
        var form = new Dictionary<string, string>
        {
            ["MessageSid"] = sid,
            ["SmsSid"] = sid,
            ["MessageStatus"] = "delivered",
            ["SmsStatus"] = "delivered",
            ["To"] = recipient.Number,
            ["AccountSid"] = credentials.Value(ProviderId, "account-sid"),
            ["ApiVersion"] = "2010-04-01",
        };

        return ingest.PostCallbackAsync(http,
            () => new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) },
            $"{uri} (SMS {sid})");
    }

    private static string E164(string number)
    {
        var digits = number.Trim();
        return digits.StartsWith('+') ? digits : "+" + PhoneNumbers.Normalize(digits);
    }

    private static string? JsonField(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
            }
        }
        return null;
    }

    /// <summary>Twilio error envelope: <c>{code, message, more_info, status}</c>.</summary>
    private static IResult TwilioError(int status, int code, string message) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message,
            ["more_info"] = $"https://www.twilio.com/docs/errors/{code}",
            ["status"] = status,
        }, statusCode: status);
}
