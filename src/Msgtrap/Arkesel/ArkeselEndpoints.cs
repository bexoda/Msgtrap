using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Msgtrap.Core;
using Msgtrap.Store;

namespace Msgtrap.Arkesel;

/// <summary>The <c>POST /api/v2/sms/send</c> body, spelled exactly as Arkesel does.</summary>
/// <remarks>
/// Fields are nullable on purpose: a missing value must produce the same
/// validation error Arkesel produces, not a deserialization failure.
/// </remarks>
public sealed class SendSmsRequest
{
    /// <summary>Sender ID / from address. Arkesel caps this at 11 characters.</summary>
    [JsonPropertyName("sender")] public string? Sender { get; set; }

    /// <summary>Message body.</summary>
    [JsonPropertyName("message")] public string? Message { get; set; }

    /// <summary>Destination numbers, international format (e.g. <c>233544919953</c>).</summary>
    [JsonPropertyName("recipients")] public List<string>? Recipients { get; set; }

    /// <summary>Optional schedule such as <c>2021-04-09 07:30 AM</c>. Captured but never delivered later.</summary>
    [JsonPropertyName("scheduled_date")] public string? ScheduledDate { get; set; }

    /// <summary>Optional delivery callback; Msgtrap calls it with <c>sms_id</c> and <c>status</c>.</summary>
    [JsonPropertyName("callback_url")] public string? CallbackUrl { get; set; }

    /// <summary>Arkesel's free sandbox marker. Recorded so the UI can label the message.</summary>
    [JsonPropertyName("sandbox")] public bool Sandbox { get; set; }

    /// <summary>Arkesel routing hint (e.g. <c>promotional</c>). Stored verbatim, otherwise unused.</summary>
    [JsonPropertyName("use_case")] public string? UseCase { get; set; }
}

/// <summary>Arkesel's error envelope: <c>{"status":"error","message":"..."}</c>.</summary>
public sealed record ErrorBody(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Status code plus body of a captured (or rejected) send.</summary>
/// <param name="Status">HTTP status to return to the caller.</param>
/// <param name="Body">Arkesel-shaped response body.</param>
/// <param name="MessageId">Id of the captured message, when one was stored.</param>
public sealed record SendResult(int Status, object Body, string? MessageId = null);

/// <summary>
/// The Arkesel SMS v2 surface: send and balance. Error wording mirrors Arkesel
/// so client error paths can be exercised for real.
/// </summary>
public sealed class ArkeselEndpoints(
    MessageStore store,
    MessageIngest ingest,
    ProviderCredentials credentials,
    HttpClient http,
    ILogger<ArkeselEndpoints> logger)
{
    /// <summary>Provider id used on the command line and in the API.</summary>
    public const string ProviderId = "arkesel";

    /// <summary>Credential field holding the <c>api-key</c> header value.</summary>
    public const string ApiKeyField = "api-key";

    /// <summary>Returned when the api-key header does not match (enforcement on).</summary>
    public const string MsgApiKey = "Invalid API Key";
    public const string MsgSender = "The sender field is required.";
    public const string MsgSenderTooLong = "The sender field must not be greater than 11 characters.";
    public const string MsgMessage = "The message field is required.";
    public const string MsgRecipients = "The recipients field is required.";
    public const string MsgInsufficient = "Insufficient balance or invalid coverage!";
    public const string MsgInactive = "Inactive SMS Gateway!";
    public const string MsgFailed = "SMS request failed!";
    public const string MsgScheduled = "SMS request sent successfully!";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Maps the Arkesel-compatible routes onto the endpoint builder.</summary>
    public void Map(IEndpointRouteBuilder app)
    {
        // The extra CancellationToken parameter keeps the compiler on the route-handler
        // overload (a bare HttpContext lambda matches RequestDelegate and drops the result).
        app.MapPost("/api/v2/sms/send", (HttpContext context, CancellationToken cancellationToken) =>
            SendAsync(context, cancellationToken));
        app.MapGet("/api/v2/clients/balance-details", Balance);
    }

    /// <summary>
    /// Enforces the api-key header when credential enforcement is enabled.
    /// Public so the OTP endpoints share exactly the same auth behaviour.
    /// </summary>
    public IResult? Authorize(HttpContext context)
    {
        var key = context.Request.Headers["api-key"].FirstOrDefault()
                  ?? context.Request.Headers["x-api-key"].FirstOrDefault();

        if (credentials.Allows(ProviderId, ApiKeyField, key)) return null;

        logger.LogWarning("Rejected {Path}: api-key {State}",
            context.Request.Path, string.IsNullOrEmpty(key) ? "missing" : "does not match");
        return Error(StatusCodes.Status403Forbidden, MsgApiKey);
    }

    /// <summary>Handles <c>POST /api/v2/sms/send</c>: auth, validation, simulation, capture.</summary>
    private async Task<IResult> SendAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (Authorize(context) is { } unauthorized) return unauthorized;

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync(cancellationToken);

        SendSmsRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SendSmsRequest>(payload, Json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Rejected /api/v2/sms/send: body is not valid JSON ({Reason})", ex.Message);
            return Error(StatusCodes.Status422UnprocessableEntity, MsgMessage);
        }
        if (request is null)
        {
            logger.LogWarning("Rejected /api/v2/sms/send: empty body");
            return Error(StatusCodes.Status422UnprocessableEntity, MsgMessage);
        }

        if (Validate(request) is { } reason)
        {
            logger.LogWarning("Rejected /api/v2/sms/send: {Reason}", reason);
            return Error(StatusCodes.Status422UnprocessableEntity, reason);
        }

        // Simulation switch: fail before capturing so the inbox only ever holds
        // messages the API actually accepted.
        var forced = store.Settings.ForceError;
        if (forced.Length > 0)
        {
            logger.LogInformation("Simulating {Code} response for /api/v2/sms/send (sender {Sender})", forced, request.Sender);
            return Error(forced switch
            {
                "402" => StatusCodes.Status402PaymentRequired,
                "403" => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status500InternalServerError,
            }, forced switch
            {
                "402" => MsgInsufficient,
                "403" => MsgInactive,
                _ => MsgFailed,
            });
        }

        var raw = JsonSerializer.Deserialize<Dictionary<string, object?>>(payload, Json) ?? [];
        var result = Send(request, raw);
        return Results.Json(result.Body, statusCode: result.Status);
    }

    /// <summary>
    /// Captures a validated Arkesel send and shapes the Arkesel response. Shared
    /// by the HTTP handler, the OTP endpoint and the UI composer.
    /// </summary>
    /// <param name="request">Validated request payload.</param>
    /// <param name="raw">Raw JSON as received, kept for the message detail view.</param>
    /// <param name="decorate">Hook applied before the message is stored.</param>
    /// <param name="otp">OTP metadata when the OTP endpoint is the caller.</param>
    public SendResult Send(
        SendSmsRequest request,
        Dictionary<string, object?> raw,
        Action<CapturedMessage>? decorate = null,
        OtpInfo? otp = null)
    {
        var result = ingest.Capture(new InboundSms(
            Provider: ProviderId,
            Sender: request.Sender ?? "",
            Body: request.Message ?? "",
            Recipients: request.Recipients ?? [],
            ScheduledFor: request.ScheduledDate,
            CallbackUrl: request.CallbackUrl,
            Sandbox: request.Sandbox,
            Raw: raw,
            Otp: otp,
            Decorate: decorate,
            Notify: NotifyAsync));

        // Arkesel returns one entry per accepted recipient, plus a single
        // "invalid numbers" bucket, and a different envelope for scheduled sends.
        if (result.Scheduled)
        {
            return new SendResult(StatusCodes.Status200OK,
                new Dictionary<string, string> { ["status"] = "success", ["message"] = MsgScheduled },
                result.Message.Id);
        }

        var data = new List<object>();
        foreach (var recipient in result.Accepted)
            data.Add(new Dictionary<string, string> { ["recipient"] = recipient.Number, ["id"] = recipient.Id });
        if (result.Invalid.Count > 0)
            data.Add(new Dictionary<string, object> { ["invalid numbers"] = result.Invalid });

        return new SendResult(StatusCodes.Status200OK,
            new Dictionary<string, object?> { ["status"] = "success", ["data"] = data },
            result.Message.Id);
    }

    /// <summary>Arkesel delivery hook: <c>POST callback_url?sms_id=...&amp;status=delivered</c>.</summary>
    private async Task NotifyAsync(string callbackUrl, Recipient recipient)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("Ignoring callback_url {Url}: only absolute http(s) URLs are supported", callbackUrl);
            return;
        }

        var target = QueryHelpers.AddQueryString(uri.ToString(), new Dictionary<string, string?>
        {
            ["sms_id"] = recipient.Id,
            ["status"] = MessageStatus.Delivered,
        });

        await ingest.PostCallbackAsync(http,
            () => new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            },
            target);
    }

    /// <summary>Reports a static, fake balance so apps that check credit keep working.</summary>
    private IResult Balance(HttpContext context)
    {
        if (Authorize(context) is { } unauthorized) return unauthorized;

        if (store.Settings.ForceError == "500")
        {
            logger.LogInformation("Simulating 500 response for /api/v2/clients/balance-details");
            return Error(StatusCodes.Status500InternalServerError, "Failed to obtain your balance details!");
        }

        var settings = store.Settings;
        logger.LogDebug("Arkesel balance requested: {SmsBalance} SMS credits, main balance {MainBalance}",
            settings.SmsBalance, settings.MainBalance);

        return Results.Json(new Dictionary<string, object?>
        {
            ["status"] = "success",
            ["data"] = new Dictionary<string, string>
            {
                ["sms_balance"] = settings.SmsBalance.ToString(),
                ["main_balance"] = settings.MainBalance,
            },
        });
    }

    /// <summary>Returns the Arkesel validation message, or <c>null</c> when valid.</summary>
    public static string? Validate(SendSmsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return MsgMessage;
        if (string.IsNullOrWhiteSpace(request.Sender)) return MsgSender;
        if (request.Recipients is not { Count: > 0 }) return MsgRecipients;
        if (request.Sender.Length > 11) return MsgSenderTooLong;
        return null;
    }

    /// <summary>Arkesel error envelope.</summary>
    public static IResult Error(int status, string message) =>
        Results.Json(new ErrorBody("error", message), statusCode: status);
}
