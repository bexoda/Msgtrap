using System.Text.Json;
using Msgtrap.Arkesel;
using Msgtrap.Core;
using Msgtrap.Providers;
using Msgtrap.Store;

namespace Msgtrap.Control;

/// <summary>
/// Control plane used by the bundled web UI: inbox listing, message deletion,
/// provider discovery, simulation settings, the compose shortcut and the live
/// event stream. This surface is not part of any provider API and is local-only.
/// </summary>
public sealed class ControlEndpoints(
    MessageStore store,
    OtpStore otps,
    ArkeselEndpoints arkesel,
    ProviderRegistry registry,
    ProviderCredentials credentials,
    AppOptions options,
    ILogger<ControlEndpoints> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Partial update for <c>PUT /api/control/settings</c>; nulls are ignored.</summary>
    public sealed class SettingsUpdate
    {
        public bool? EnforceCredentials { get; set; }
        public string? ForceError { get; set; }
        public int? DeliverAfterMs { get; set; }
        public int? SmsBalance { get; set; }
        public string? MainBalance { get; set; }
        public List<string>? DisabledProviders { get; set; }
    }

    /// <summary>Maps the control routes onto the endpoint builder.</summary>
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/control/messages", (string? q, string? status, string? sender, string? recipient, string? provider) =>
        {
            var messages = store.List(new MessageFilter(q, status, sender, recipient));
            if (!string.IsNullOrEmpty(provider))
                messages = messages.Where(message => message.Provider == provider).ToList();

            logger.LogDebug("Listing {Count} message(s) with filter q={Query} status={Status} provider={Provider}",
                messages.Count, q, status, provider);
            return Results.Json(new { total = messages.Count, messages });
        });

        app.MapGet("/api/control/messages/{id}", (string id) =>
            store.Get(id) is { } message ? Results.Json(message) : Results.NotFound());

        app.MapDelete("/api/control/messages/{id}", (string id) =>
        {
            if (!store.Delete(id)) return Results.NotFound();
            logger.LogInformation("Deleted {MessageId} from the inbox", id);
            return Results.NoContent();
        });

        app.MapDelete("/api/control/messages", () =>
        {
            store.Clear();
            // Outstanding OTPs reference cleared messages, so drop them too.
            otps.Clear();
            logger.LogInformation("Inbox cleared (messages and outstanding OTPs)");
            return Results.NoContent();
        });

        // Everything the UI needs to describe a provider: routes, credentials, state.
        app.MapGet("/api/control/providers", () => Results.Json(new
        {
            baseUrl = options.BaseUrl,
            startupRestricted = registry.StartupRestricted,
            providers = registry.Describe(store, credentials),
        }));

        app.MapGet("/api/control/settings", () => Results.Json(store.Settings));

        app.MapPut("/api/control/settings", (SettingsUpdate update) =>
        {
            store.UpdateSettings(settings =>
            {
                if (update.EnforceCredentials is { } enforce) settings.EnforceCredentials = enforce;
                if (update.ForceError is { } force) settings.ForceError = force;
                if (update.DeliverAfterMs is { } delay) settings.DeliverAfterMs = delay;
                if (update.SmsBalance is { } balance) settings.SmsBalance = balance;
                if (update.MainBalance is { } main) settings.MainBalance = main;
                if (update.DisabledProviders is { } disabled)
                {
                    // Only providers loaded at startup can be toggled.
                    settings.DisabledProviders = disabled
                        .Where(id => registry.Find(id) is not null)
                        .Distinct()
                        .ToList();
                }
            });

            var applied = store.Settings;
            logger.LogInformation(
                "Settings updated: enforceCredentials={Enforce} forceError={ForceError} deliverAfterMs={Delay} disabled={Disabled}",
                applied.EnforceCredentials,
                string.IsNullOrEmpty(applied.ForceError) ? "none" : applied.ForceError,
                applied.DeliverAfterMs,
                applied.DisabledProviders.Count == 0 ? "none" : string.Join(",", applied.DisabledProviders));

            return Results.Json(applied);
        });

        // The UI composer goes through the same capture path as the public API,
        // always shaped as an Arkesel response so the panel can show the envelope.
        app.MapPost("/api/control/send", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync(context.RequestAborted);

            SendSmsRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<SendSmsRequest>(payload, Json);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Composer rejected: body is not valid JSON ({Reason})", ex.Message);
                return Results.BadRequest(new { message = ArkeselEndpoints.MsgMessage });
            }
            if (request is null)
                return Results.BadRequest(new { message = ArkeselEndpoints.MsgMessage });

            if (ArkeselEndpoints.Validate(request) is { } reason)
                return Results.BadRequest(new { message = reason });

            logger.LogDebug("Composer sending from {Sender} to {Recipients}",
                request.Sender, request.Recipients?.Count ?? 0);

            var raw = JsonSerializer.Deserialize<Dictionary<string, object?>>(payload, Json) ?? [];
            var result = arkesel.Send(request, raw);
            return Results.Json(result.Body, statusCode: result.Status);
        });

        app.MapGet("/api/control/events", StreamEvents);
    }

    /// <summary>Server-sent events so the inbox updates without polling.</summary>
    private async Task StreamEvents(HttpContext context)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = store.Subscribe();
        var cancellation = context.RequestAborted;

        logger.LogDebug("SSE client connected");
        await Write(context, ": connected\n\n");

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var ready = subscription.Reader.WaitToReadAsync(cancellation).AsTask();
                var heartbeat = Task.Delay(TimeSpan.FromSeconds(20), cancellation);

                // Heartbeat comments keep proxies and browsers from dropping idle streams.
                if (await Task.WhenAny(ready, heartbeat) == heartbeat)
                {
                    await Write(context, ": ping\n\n");
                    continue;
                }

                if (!await ready) break;

                while (subscription.Reader.TryRead(out var evt))
                    await Write(context, $"data: {JsonSerializer.Serialize(evt, Json)}\n\n");
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
        finally
        {
            logger.LogDebug("SSE client disconnected");
        }
    }

    private static async Task Write(HttpContext context, string text)
    {
        await context.Response.WriteAsync(text, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}
