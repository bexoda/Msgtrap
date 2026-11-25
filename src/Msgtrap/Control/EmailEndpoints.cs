using System.Text.Json;
using Msgtrap.Store;

namespace Msgtrap.Control;

/// <summary>
/// Control plane for the email half of the inbox: listing, reading, deleting and
/// the live event stream the UI subscribes to. Like
/// <see cref="ControlEndpoints"/> this is local UI plumbing, not a mail protocol.
/// </summary>
public sealed class EmailEndpoints(
    EmailStore store,
    SmtpStatus smtp,
    ILogger<EmailEndpoints> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Maps the email control routes onto the endpoint builder.</summary>
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/control/emails", (string? q, string? from) =>
        {
            var emails = store.List(new EmailFilter(q, from));
            logger.LogDebug("Listing {Count} email(s) with filter q={Query} from={From}", emails.Count, q, from);
            return Results.Json(new { total = emails.Count, emails });
        });

        app.MapGet("/api/control/emails/{id}", (string id) =>
            store.Get(id) is { } email ? Results.Json(email) : Results.NotFound());

        app.MapDelete("/api/control/emails/{id}", (string id) =>
        {
            if (!store.Delete(id)) return Results.NotFound();
            logger.LogInformation("Deleted email {EmailId} from the inbox", id);
            return Results.NoContent();
        });

        app.MapDelete("/api/control/emails", () =>
        {
            store.Clear();
            logger.LogInformation("Email inbox cleared");
            return Results.NoContent();
        });

        // The UI shows the SMTP address so people know where to point their app.
        app.MapGet("/api/control/smtp", () => Results.Json(smtp.Describe()));

        app.MapGet("/api/control/emails/events", StreamEvents);
    }

    /// <summary>Server-sent events so new mail appears without polling.</summary>
    private async Task StreamEvents(HttpContext context)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = store.Subscribe();
        var cancellation = context.RequestAborted;

        logger.LogDebug("Email SSE client connected");
        await Write(context, ": connected\n\n");

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var ready = subscription.Reader.WaitToReadAsync(cancellation).AsTask();
                var heartbeat = Task.Delay(TimeSpan.FromSeconds(20), cancellation);

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
            logger.LogDebug("Email SSE client disconnected");
        }
    }

    private static async Task Write(HttpContext context, string text)
    {
        await context.Response.WriteAsync(text, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}

/// <summary>
/// Resolves the SMTP endpoint at request time. The listener is created during
/// startup, so this indirection lets the control plane report the real port
/// (including an ephemeral one chosen for tests) without depending on order.
/// </summary>
public sealed class SmtpStatus(AppOptions options, IServiceProvider services)
{
    /// <summary>Describes the SMTP capture endpoint for the UI.</summary>
    public object Describe()
    {
        var server = services.GetService<Core.SmtpServer>();
        var listening = server?.IsListening ?? false;
        var port = server is { IsListening: true } ? server.Port : options.SmtpPort;

        return new
        {
            enabled = listening,
            host = options.SmtpHost,
            port,
            address = $"{options.SmtpHost}:{port}",
            hint = listening
                ? "Point your app's SMTP host here. No TLS, no credentials required."
                : server?.DisabledReason ?? "Email capture is off.",
        };
    }
}
