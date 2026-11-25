namespace Msgtrap.Store;

/// <summary>One captured email, as received over SMTP.</summary>
public sealed class CapturedEmail
{
    /// <summary>Internal id (also used in the queue confirmation).</summary>
    public string Id { get; set; } = "";

    /// <summary>From header, rendered for display (e.g. <c>Ada &lt;ada@example.com&gt;</c>).</summary>
    public string From { get; set; } = "";

    /// <summary>To header values.</summary>
    public List<string> To { get; set; } = [];

    /// <summary>Cc header values.</summary>
    public List<string> Cc { get; set; } = [];

    /// <summary>
    /// Recipients the SMTP envelope accepted but that the headers did not mention,
    /// i.e. the Bcc list.
    /// </summary>
    public List<string> Bcc { get; set; } = [];

    /// <summary>Decoded Subject header.</summary>
    public string Subject { get; set; } = "";

    /// <summary>When the message finished transferring (UTC).</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Decoded <c>text/plain</c> body, if the message had one.</summary>
    public string TextBody { get; set; } = "";

    /// <summary>Decoded <c>text/html</c> body, if the message had one.</summary>
    public string HtmlBody { get; set; } = "";

    /// <summary>All top-level headers, in order, so duplicates and oddities survive.</summary>
    public List<EmailHeader> Headers { get; set; } = [];

    /// <summary>Attachments found anywhere in the MIME tree.</summary>
    public List<EmailAttachment> Attachments { get; set; } = [];

    /// <summary>Raw message size in bytes, as transferred.</summary>
    public int SizeBytes { get; set; }

    /// <summary>Envelope sender supplied via <c>MAIL FROM</c>.</summary>
    public string EnvelopeFrom { get; set; } = "";

    /// <summary>Envelope recipients supplied via <c>RCPT TO</c>.</summary>
    public List<string> EnvelopeTo { get; set; } = [];

    /// <summary>The full message source, for the "Raw source" view.</summary>
    public string RawSource { get; set; } = "";

    /// <summary>True when an HTML body is available.</summary>
    public bool HasHtml => HtmlBody.Length > 0;

    /// <summary>Deep copy, so the store never hands out live instances.</summary>
    public CapturedEmail Clone() => new()
    {
        Id = Id,
        From = From,
        To = [.. To],
        Cc = [.. Cc],
        Bcc = [.. Bcc],
        Subject = Subject,
        ReceivedAt = ReceivedAt,
        TextBody = TextBody,
        HtmlBody = HtmlBody,
        Headers = [.. Headers],
        Attachments = [.. Attachments],
        SizeBytes = SizeBytes,
        EnvelopeFrom = EnvelopeFrom,
        EnvelopeTo = [.. EnvelopeTo],
        RawSource = RawSource,
    };
}

/// <summary>A single header line, name and decoded value.</summary>
public sealed record EmailHeader(string Name, string Value);

/// <summary>An attachment descriptor. Contents are not stored separately.</summary>
public sealed record EmailAttachment(string FileName, string ContentType, int SizeBytes);

/// <summary>Filter for listing captured email.</summary>
public sealed record EmailFilter(string? Query, string? From = null);

/// <summary>A change pushed to email subscribers.</summary>
public sealed record EmailEvent(string Type, CapturedEmail? Email = null, string? Id = null);
