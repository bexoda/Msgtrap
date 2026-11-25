using System.Text.Json;
using Msgtrap.Core;
using Msgtrap.Store;

namespace Msgtrap.Providers;

/// <summary>
/// mNotify adapter: <c>POST /api/sms/quick?key=...</c> and
/// <c>GET /api/balance/sms?key=...</c>.
/// </summary>
/// <remarks>
/// mNotify authenticates with an <c>API key</c> query parameter and takes a JSON
/// body <c>{recipient[], sender, message, is_schedule, schedule_date, sms_type}</c>.
/// The success envelope (<c>status</c>/<c>code</c>/<c>message</c>/<c>summary</c>)
/// and the balance shape follow their published OpenAPI spec
/// (<c>developer.bms.africa/openapi15.yaml</c>). Error bodies are Msgtrap-defined
/// in the same envelope, because the spec only documents 200.
/// </remarks>
public sealed class MnotifyProvider(
    MessageStore store,
    MessageIngest ingest,
    ProviderCredentials credentials,
    ILogger<MnotifyProvider> logger) : ISmsProvider
{
    /// <summary>Provider id.</summary>
    public const string ProviderId = "mnotify";

    /// <summary>Credential field holding the <c>key</c> query parameter.</summary>
    public const string ApiKeyField = "key";

    public string Id => ProviderId;
    public string DisplayName => "mNotify";
    public string Summary => "mNotify / BMS SMS API v2 (Ghana)";
    public string DocsUrl => "https://developer.bms.africa/";

    public IReadOnlyList<ProviderEndpoint> Endpoints { get; } =
    [
        new("POST", "/api/sms/quick?key=...", "Quick bulk SMS with a JSON body"),
        new("GET", "/api/balance/sms?key=...", "Fake SMS credit balance"),
    ];

    public IReadOnlyList<CredentialField> Credentials { get; } =
    [
        new("key", "API key", "key query parameter"),
    ];

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sms/quick", (HttpContext context, CancellationToken cancellationToken) =>
            SendAsync(context, cancellationToken));
        app.MapGet("/api/balance/sms", Balance);
    }

    private async Task<IResult> SendAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (ProviderGuard.IsDisabled(store, ProviderId)) return ProviderGuard.Disabled(ProviderId);

        var key = context.Request.Query["key"].FirstOrDefault();
        if (!credentials.Allows(ProviderId, ApiKeyField, key))
        {
            logger.LogWarning("Rejected mNotify send: key {State}", key is null ? "missing" : "does not match");
            return Error(StatusCodes.Status401Unauthorized, "1001", "Invalid API key");
        }

        QuickSmsRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<QuickSmsRequest>(
                context.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Rejected mNotify send: body is not valid JSON ({Reason})", ex.Message);
            return Error(StatusCodes.Status422UnprocessableEntity, "1002", "The message field is required.");
        }

        if (request is null)
            return Error(StatusCodes.Status422UnprocessableEntity, "1002", "The message field is required.");
        if (string.IsNullOrWhiteSpace(request.Message))
            return Error(StatusCodes.Status422UnprocessableEntity, "1002", "The message field is required.");
        if (string.IsNullOrWhiteSpace(request.Sender))
            return Error(StatusCodes.Status422UnprocessableEntity, "1003", "The sender field is required.");
        if (request.Recipient is not { Count: > 0 })
            return Error(StatusCodes.Status422UnprocessableEntity, "1004", "The recipient field is required.");
        if (request.Sender.Length > 11)
            return Error(StatusCodes.Status422UnprocessableEntity, "1005",
                "The sender field must not be greater than 11 characters.");

        var raw = new Dictionary<string, object?>
        {
            ["recipient"] = request.Recipient,
            ["sender"] = request.Sender,
            ["message"] = request.Message,
            ["is_schedule"] = request.IsSchedule,
            ["schedule_date"] = request.ScheduleDate,
            ["sms_type"] = request.SmsType,
        };

        var result = ingest.Capture(new InboundSms(
            Provider: ProviderId,
            Sender: request.Sender,
            Body: request.Message,
            Recipients: request.Recipient,
            ScheduledFor: request.IsSchedule == true ? request.ScheduleDate : null,
            Raw: raw));

        // Campaign id: mNotify returns an upper-case GUID under `_id`.
        var campaignId = result.Message.Id.ToUpperInvariant();

        return Results.Json(new Dictionary<string, object?>
        {
            ["status"] = "success",
            ["code"] = "2000",
            ["message"] = "messages sent successfully",
            ["summary"] = new Dictionary<string, object?>
            {
                ["_id"] = campaignId,
                ["type"] = "API QUICK SMS",
                ["total_sent"] = result.Accepted.Count,
                ["international_dropped"] = 0,
                ["contacts"] = request.Recipient.Count,
                ["total_rejected"] = result.Invalid.Count,
                ["numbers_sent"] = result.Accepted.Select(recipient => recipient.Number).ToList(),
                ["credit_used"] = result.Message.Segments * result.Accepted.Count,
                ["credit_left"] = Math.Max(0, store.Settings.SmsBalance - (result.Message.Segments * result.Accepted.Count)),
            },
        });
    }

    /// <summary>mNotify balance: <c>{status, balance, bonus}</c>.</summary>
    private IResult Balance(HttpContext context)
    {
        if (ProviderGuard.IsDisabled(store, ProviderId)) return ProviderGuard.Disabled(ProviderId);

        var key = context.Request.Query["key"].FirstOrDefault();
        if (!credentials.Allows(ProviderId, ApiKeyField, key))
            return Error(StatusCodes.Status401Unauthorized, "1001", "Invalid API key");

        var settings = store.Settings;
        return Results.Json(new Dictionary<string, object?>
        {
            ["status"] = "success",
            ["balance"] = settings.SmsBalance,
            ["bonus"] = 0,
        });
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["status"] = "error",
            ["code"] = code,
            ["message"] = message,
        }, statusCode: status);

    /// <summary>The <c>/api/sms/quick</c> body.</summary>
    public sealed class QuickSmsRequest
    {
        /// <summary>Destination numbers; mNotify accepts local or international format.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("recipient")]
        public List<string>? Recipient { get; set; }

        /// <summary>Sender ID, 11 characters max.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("sender")]
        public string? Sender { get; set; }

        /// <summary>Message text.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string? Message { get; set; }

        /// <summary>True to schedule instead of sending immediately.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("is_schedule")]
        public bool? IsSchedule { get; set; }

        /// <summary><c>YYYY-MM-DD hh:mm</c> when <see cref="IsSchedule"/> is true.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("schedule_date")]
        public string? ScheduleDate { get; set; }

        /// <summary>Optional blast type, e.g. <c>otp</c>. Recorded only.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("sms_type")]
        public string? SmsType { get; set; }
    }
}
