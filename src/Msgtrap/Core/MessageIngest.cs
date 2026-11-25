using Msgtrap.Store;

namespace Msgtrap.Core;

/// <summary>
/// Provider-neutral description of one send request. Each provider adapter
/// translates its own wire format into this, and <see cref="MessageIngest"/>
/// does the rest.
/// </summary>
/// <param name="Provider">Adapter id, e.g. <c>arkesel</c>.</param>
/// <param name="Sender">Sender ID / from address.</param>
/// <param name="Body">Message text.</param>
/// <param name="Recipients">Raw destination numbers, before normalization.</param>
/// <param name="ScheduledFor">Requested schedule, if the provider supports one.</param>
/// <param name="CallbackUrl">Delivery-notification URL supplied by the caller.</param>
/// <param name="Sandbox">True when the caller asked for the provider's sandbox mode.</param>
/// <param name="Raw">Original request as received, shown in the message detail view.</param>
/// <param name="Otp">OTP metadata when the message carries a one-time code.</param>
/// <param name="Decorate">Hook applied before the message is stored.</param>
/// <param name="Notify">
/// Provider-specific delivery notification. Called once per recipient after the
/// simulated delivery, with the callback URL and the recipient.
/// </param>
public sealed record InboundSms(
    string Provider,
    string Sender,
    string Body,
    IReadOnlyList<string> Recipients,
    string? ScheduledFor = null,
    string? CallbackUrl = null,
    bool Sandbox = false,
    Dictionary<string, object?>? Raw = null,
    OtpInfo? Otp = null,
    Action<CapturedMessage>? Decorate = null,
    Func<string, Recipient, Task>? Notify = null);

/// <summary>Outcome of ingesting a send request.</summary>
/// <param name="Message">The stored message.</param>
/// <param name="Accepted">Recipients that passed validation, each with its own id.</param>
/// <param name="Invalid">Numbers that failed validation, in request order.</param>
/// <param name="Scheduled">True when the message was stored without a delivery timer.</param>
public sealed record IngestResult(
    CapturedMessage Message,
    IReadOnlyList<Recipient> Accepted,
    IReadOnlyList<string> Invalid,
    bool Scheduled);

/// <summary>
/// The single place where messages are created, delivered and reported.
/// </summary>
/// <remarks>
/// Providers differ only in auth, field names and response shape. Everything
/// true of every SMS - number normalization, segment counting, storage, the
/// queued-to-delivered transition and callback timing - lives here, so adding a
/// provider is a mapping exercise rather than a reimplementation.
/// </remarks>
public sealed class MessageIngest(MessageStore store, ILogger<MessageIngest> logger)
{
    /// <summary>Validates, stores and schedules delivery for one send request.</summary>
    public IngestResult Capture(InboundSms input)
    {
        var analysis = SmsAnalyzer.Analyze(input.Body);
        var now = DateTimeOffset.UtcNow;
        var settings = store.Settings;

        var message = new CapturedMessage
        {
            Id = Guid.NewGuid().ToString(),
            Provider = input.Provider,
            Sender = input.Sender,
            Body = input.Body,
            Encoding = analysis.Encoding,
            Chars = analysis.Chars,
            Segments = analysis.Segments,
            Sandbox = input.Sandbox,
            ScheduledFor = string.IsNullOrWhiteSpace(input.ScheduledFor) ? null : input.ScheduledFor.Trim(),
            CallbackUrl = string.IsNullOrWhiteSpace(input.CallbackUrl) ? null : input.CallbackUrl.Trim(),
            Otp = input.Otp,
            CreatedAt = now,
            DeliveryDelayMs = settings.DeliverAfterMs,
            Request = input.Raw,
            DeliveryLog =
            [
                new DeliveryLogEntry
                {
                    At = now,
                    Status = MessageStatus.Queued,
                    Message = $"Accepted by Msgtrap ({input.Provider})",
                },
            ],
        };

        var accepted = new List<Recipient>();
        foreach (var recipient in input.Recipients)
        {
            var number = PhoneNumbers.Normalize(recipient);
            if (!PhoneNumbers.IsValid(number))
            {
                message.Invalid.Add(recipient);
                continue;
            }
            var stored = new Recipient
            {
                Number = number,
                Id = Guid.NewGuid().ToString(),
                Status = MessageStatus.Queued,
            };
            message.Recipients.Add(stored);
            accepted.Add(stored.Clone());
        }

        input.Decorate?.Invoke(message);

        if (message.ScheduledFor is not null)
        {
            message.Status = MessageStatus.Scheduled;
            message.DeliveryLog.Add(new DeliveryLogEntry
            {
                At = now,
                Status = MessageStatus.Scheduled,
                Message = $"Scheduled for {message.ScheduledFor} (Msgtrap holds it without delivery)",
            });
            store.Add(message);
            logger.LogInformation(
                "Scheduled {Provider} message {MessageId} from {Sender} for {ScheduledFor} ({Recipients} recipient(s))",
                input.Provider, message.Id, message.Sender, message.ScheduledFor, accepted.Count);
            return new IngestResult(message, accepted, message.Invalid, true);
        }

        store.Add(message);
        _ = DeliverAsync(message.Id, TimeSpan.FromMilliseconds(settings.DeliverAfterMs), input.Notify);

        logger.LogInformation(
            "Captured {Provider} message {MessageId}: {Sender} -> {Recipients} recipient(s), {Segments}x{Encoding} ({Chars} chars)",
            input.Provider, message.Id, message.Sender, accepted.Count,
            message.Segments, message.Encoding, message.Chars);

        if (message.Invalid.Count > 0)
            logger.LogWarning("{MessageId}: rejected {InvalidCount} invalid number(s): {Numbers}",
                message.Id, message.Invalid.Count, string.Join(", ", message.Invalid));

        return new IngestResult(message, accepted, message.Invalid, false);
    }

    /// <summary>Flips a queued message to delivered after the configured delay.</summary>
    private async Task DeliverAsync(string id, TimeSpan delay, Func<string, Recipient, Task>? notify)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay);

            var deliveredAt = DateTimeOffset.UtcNow;
            List<Recipient> delivered = [];
            string? callbackUrl = null;

            var updated = store.Update(id, message =>
            {
                message.Status = MessageStatus.Delivered;
                message.DeliveredAt = deliveredAt;
                foreach (var recipient in message.Recipients)
                {
                    recipient.Status = MessageStatus.Delivered;
                    delivered.Add(recipient.Clone());
                }
                callbackUrl = message.CallbackUrl;
                message.DeliveryLog.Add(new DeliveryLogEntry
                {
                    At = deliveredAt,
                    Status = MessageStatus.Delivered,
                    Message = "Delivered to simulated handset",
                });
            });

            if (!updated) return; // message was cleared while queued

            logger.LogInformation("Delivered {MessageId} to {Recipients} simulated handset(s) after {Delay} ms",
                id, delivered.Count, delay.TotalMilliseconds);

            if (notify is null || string.IsNullOrWhiteSpace(callbackUrl)) return;
            foreach (var recipient in delivered)
            {
                try
                {
                    await notify(callbackUrl, recipient);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Delivery notification for {MessageId} to {Url} failed: {Message}",
                        id, callbackUrl, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delivery simulation failed for {MessageId}", id);
        }
    }

    /// <summary>
    /// Fires an HTTP callback and logs the outcome. Providers shape their own
    /// payload; this only owns the "never fail the delivery" behaviour.
    /// </summary>
    public async Task PostCallbackAsync(HttpClient http, Func<HttpRequestMessage> build, string description)
    {
        try
        {
            using var request = build();
            using var response = await http.SendAsync(request);
            logger.LogInformation("Delivery callback {Description} -> {Status}", description, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Delivery callback {Description} failed: {Message}", description, ex.Message);
        }
    }
}
