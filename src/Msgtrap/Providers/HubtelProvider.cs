using System.Text.Json;
using Msgtrap.Core;
using Msgtrap.Store;

namespace Msgtrap.Providers;

/// <summary>
/// Hubtel adapter: <c>/v1/messages/send</c>.
/// </summary>
/// <remarks>
/// Hubtel's documented call is either
/// <c>POST https://smsc.hubtel.com/v1/messages/send</c> with Basic auth and a JSON
/// body <c>{from, to, content}</c>, or the older
/// <c>GET ...?ClientId=&amp;ClientSecret=&amp;From=&amp;To=&amp;Content=</c> form.
/// Both are accepted, because real integrations still use both. Response fields
/// (<c>MessageId</c>, <c>Status</c>, <c>Rate</c>, <c>NetworkId</c>) follow the
/// published shape; error bodies are Msgtrap-defined since Hubtel does not
/// document them publicly.
/// </remarks>
public sealed class HubtelProvider(
    MessageStore store,
    MessageIngest ingest,
    ProviderCredentials credentials,
    HttpClient http,
    ILogger<HubtelProvider> logger) : ISmsProvider
{
    /// <summary>Provider id.</summary>
    public const string ProviderId = "hubtel";

    public string Id => ProviderId;
    public string DisplayName => "Hubtel";
    public string Summary => "Hubtel SMS API (Ghana)";
    public string DocsUrl => "https://developers.hubtel.com/";

    public IReadOnlyList<ProviderEndpoint> Endpoints { get; } =
    [
        new("POST", "/v1/messages/send", "Basic auth + JSON {from, to, content}"),
        new("GET", "/v1/messages/send", "Legacy query-string form with client credentials"),
    ];

    public IReadOnlyList<CredentialField> Credentials { get; } =
    [
        new("client-id", "Client ID", "basic auth user or ClientId query"),
        new("client-secret", "Client secret", "basic auth password or ClientSecret query"),
    ];

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/messages/send", (HttpContext context, CancellationToken cancellationToken) =>
            SendAsync(context, cancellationToken));
        app.MapGet("/v1/messages/send", (HttpContext context, CancellationToken cancellationToken) =>
            SendAsync(context, cancellationToken));
    }

    private async Task<IResult> SendAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (ProviderGuard.IsDisabled(store, ProviderId)) return ProviderGuard.Disabled(ProviderId);

        // Credentials arrive either as Basic auth (modern) or as query/form fields (legacy).
        var fields = await RequestFields.QueryOrFormAsync(context, cancellationToken);
        var basic = context.BasicAuth();
        var clientId = basic?.User ?? fields.Pick("ClientId", "clientid", "client_id");
        var clientSecret = basic?.Password ?? fields.Pick("ClientSecret", "clientsecret", "client_secret");

        if (!credentials.AllowsAll(ProviderId,
                ("client-id", clientId),
                ("client-secret", clientSecret)))
        {
            logger.LogWarning("Rejected Hubtel send: client credentials {State}",
                clientId is null ? "missing" : "do not match");
            return Results.Json(new Dictionary<string, object?>
            {
                ["Status"] = "Unauthorized",
                ["Message"] = "Invalid ClientId or ClientSecret",
            }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var from = fields.Pick("From", "from", "sender");
        var to = fields.Pick("To", "to", "recipient", "number");
        var content = fields.Pick("Content", "content", "message", "body");

        // JSON bodies are also accepted; merge them over the query/form values.
        if (context.Request.HasJsonContentType())
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                from = JsonField(document.RootElement, "from", "From", "sender") ?? from;
                to = JsonField(document.RootElement, "to", "To", "recipient", "number") ?? to;
                content = JsonField(document.RootElement, "content", "Content", "message", "body") ?? content;
            }
        }

        if (string.IsNullOrWhiteSpace(to))
            return HubtelError(StatusCodes.Status400BadRequest, "The To field is required.");
        if (string.IsNullOrWhiteSpace(content))
            return HubtelError(StatusCodes.Status400BadRequest, "The Content field is required.");

        // Keep the raw view faithful whichever way the fields arrived.
        fields["from"] = from ?? "";
        fields["to"] = to;
        fields["content"] = content;

        var result = ingest.Capture(new InboundSms(
            Provider: ProviderId,
            Sender: from ?? "",
            Body: content,
            Recipients: to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            CallbackUrl: fields.Pick("Callback", "callback", "callbackUrl"),
            Raw: fields.ToDictionary(entry => entry.Key, entry => (object?)entry.Value),
            Notify: NotifyAsync));

        if (result.Accepted.Count == 0)
            return HubtelError(StatusCodes.Status400BadRequest, "No valid recipient numbers were supplied.");

        // Hubtel answers with one message per request, so echo the first id.
        return Results.Json(new Dictionary<string, object?>
        {
            ["MessageId"] = result.Accepted[0].Id,
            ["Status"] = "Success",
            ["Rate"] = 1,
            ["NetworkId"] = "62002",
        });
    }

    /// <summary>Hubtel delivery receipt: <c>GET callback?MessageId=...&amp;Status=Success</c>.</summary>
    private Task NotifyAsync(string callbackUrl, Recipient recipient)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("Ignoring Hubtel callback {Url}: only absolute http(s) URLs are supported", callbackUrl);
            return Task.CompletedTask;
        }

        var target = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(uri.ToString(), new Dictionary<string, string?>
        {
            ["MessageId"] = recipient.Id,
            ["Status"] = "Success",
            ["To"] = recipient.Number,
        });

        return ingest.PostCallbackAsync(http,
            () => new HttpRequestMessage(HttpMethod.Get, target),
            target);
    }

    private static string? JsonField(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => property.Value.ToString(),
                };
            }
        }
        return null;
    }

    private static IResult HubtelError(int status, string message) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["Status"] = status == StatusCodes.Status401Unauthorized ? "Unauthorized" : "BadRequest",
            ["Message"] = message,
        }, statusCode: status);
}
