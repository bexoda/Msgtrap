using System.Text;
using Msgtrap.Store;

namespace Msgtrap.Core;

/// <summary>
/// A deliberately small RFC 5322 / MIME reader: enough to show what a developer
/// sent, without pulling in a MIME library.
/// </summary>
/// <remarks>
/// Supported: header unfolding, encoded-word subjects/addresses (B and Q),
/// base64 and quoted-printable transfer encodings, UTF-8 / Latin-1 / ASCII
/// charsets, and the multipart/alternative, /mixed and /related container types.
/// Not supported: nested message/rfc822 bodies, attachment content extraction
/// (only the filename and size are recorded), and exotic charsets.
/// </remarks>
public static class MimeParser
{
    /// <summary>Parses a raw SMTP message into a <see cref="CapturedEmail"/>.</summary>
    /// <param name="rawSource">Message source after dot-unstuffing.</param>
    /// <param name="envelopeTo">Recipients accepted via <c>RCPT TO</c>.</param>
    /// <param name="envelopeFrom">Sender from <c>MAIL FROM</c>.</param>
    /// <param name="receivedAt">When the transfer completed.</param>
    public static CapturedEmail Parse(
        string rawSource,
        IReadOnlyList<string> envelopeTo,
        string envelopeFrom,
        DateTimeOffset receivedAt)
    {
        var raw = rawSource.Replace("\r\n", "\n").Replace('\r', '\n');
        var (headerBlock, body) = SplitHeaders(raw);

        var headers = ParseHeaders(headerBlock);
        var contentType = Header(headers, "content-type") ?? "text/plain";
        var transferEncoding = Header(headers, "content-transfer-encoding") ?? "";

        var text = "";
        var html = "";
        var attachments = new List<EmailAttachment>();
        Walk(contentType, transferEncoding, body, headers, ref text, ref html, attachments);

        var to = AddressList(Header(headers, "to"));
        var cc = AddressList(Header(headers, "cc"));
        var declared = to.Concat(cc)
            .Select(ExtractAddress)
            .Where(address => address.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Anything the envelope accepted but the headers never mention is a Bcc.
        var bcc = envelopeTo
            .Where(recipient => !declared.Contains(ExtractAddress(recipient)))
            .ToList();

        return new CapturedEmail
        {
            Id = Guid.NewGuid().ToString(),
            From = DecodeWords(Header(headers, "from") ?? envelopeFrom),
            To = to.Select(DecodeWords).ToList(),
            Cc = cc.Select(DecodeWords).ToList(),
            Bcc = bcc,
            Subject = DecodeWords(Header(headers, "subject") ?? ""),
            ReceivedAt = receivedAt,
            TextBody = text,
            HtmlBody = html,
            Headers = headers,
            Attachments = attachments,
            SizeBytes = Encoding.Latin1.GetByteCount(rawSource),
            EnvelopeFrom = envelopeFrom,
            EnvelopeTo = [.. envelopeTo],
            RawSource = rawSource,
        };
    }

    private static (string Headers, string Body) SplitHeaders(string raw)
    {
        var index = raw.IndexOf("\n\n", StringComparison.Ordinal);
        return index < 0 ? (raw, "") : (raw[..index], raw[(index + 2)..]);
    }

    /// <summary>Parses a header block, joining folded continuation lines.</summary>
    private static List<EmailHeader> ParseHeaders(string block)
    {
        var headers = new List<EmailHeader>();
        if (block.Length == 0) return headers;

        string? name = null;
        var value = new StringBuilder();

        void Flush()
        {
            if (name is not null) headers.Add(new EmailHeader(name, value.ToString().Trim()));
            name = null;
            value.Clear();
        }

        foreach (var line in block.Split('\n'))
        {
            if (line.Length == 0) continue;

            // A line starting with space/tab continues the previous header value.
            if ((line[0] == ' ' || line[0] == '\t') && name is not null)
            {
                value.Append(' ').Append(line.Trim());
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            Flush();
            name = line[..colon].Trim();
            value.Append(line[(colon + 1)..].Trim());
        }

        Flush();
        return headers;
    }

    private static string? Header(List<EmailHeader> headers, string name) =>
        headers.FirstOrDefault(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Splits <c>a, "b, c" &lt;d@e&gt;</c> respecting quoted names.</summary>
    public static List<string> AddressList(string? value)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) return list;

        var current = new StringBuilder();
        var inQuotes = false;
        var angleDepth = 0;

        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    current.Append(c);
                    break;
                case '<' when !inQuotes:
                    angleDepth++;
                    current.Append(c);
                    break;
                case '>' when !inQuotes:
                    angleDepth = Math.Max(0, angleDepth - 1);
                    current.Append(c);
                    break;
                case ',' when !inQuotes && angleDepth == 0:
                    if (current.ToString().Trim() is { Length: > 0 } part) list.Add(part);
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (current.ToString().Trim() is { Length: > 0 } last) list.Add(last);
        return list;
    }

    /// <summary>Pulls the bare address out of <c>Name &lt;a@b&gt;</c>.</summary>
    public static string ExtractAddress(string value)
    {
        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        var address = open >= 0 && close > open ? value[(open + 1)..close] : value;
        return address.Trim().Trim('"').ToLowerInvariant();
    }

    private static void Walk(
        string contentType,
        string transferEncoding,
        string body,
        List<EmailHeader> partHeaders,
        ref string text,
        ref string html,
        List<EmailAttachment> attachments)
    {
        var (mediaType, parameters) = ParseContentType(contentType);

        if (mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
            && parameters.GetValueOrDefault("boundary") is { Length: > 0 } boundary)
        {
            foreach (var part in SplitByBoundary(body, boundary))
            {
                var (partHeaderBlock, partBody) = SplitHeaders(part);
                var headers = ParseHeaders(partHeaderBlock);
                Walk(
                    Header(headers, "content-type") ?? "text/plain",
                    Header(headers, "content-transfer-encoding") ?? "",
                    partBody,
                    headers,
                    ref text,
                    ref html,
                    attachments);
            }
            return;
        }

        var charset = parameters.GetValueOrDefault("charset");
        var decoded = DecodeBody(body, transferEncoding, charset);

        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
        {
            if (html.Length == 0) html = decoded;
            return;
        }

        if (mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Length == 0) text = decoded;
            return;
        }

        // Everything else is treated as an attachment we describe but do not decode.
        var fileName = Parameter(Header(partHeaders, "content-disposition"), "filename")
                       ?? parameters.GetValueOrDefault("name")
                       ?? "";
        if (fileName.Length > 0 || mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase))
        {
            attachments.Add(new EmailAttachment(
                DecodeWords(fileName),
                mediaType,
                Encoding.Latin1.GetByteCount(body)));
        }
    }

    /// <summary>Splits a multipart body on its boundary markers.</summary>
    private static List<string> SplitByBoundary(string body, string boundary)
    {
        var parts = new List<string>();
        var marker = "--" + boundary;
        var current = new StringBuilder();
        var inPart = false;

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                if (inPart) parts.Add(current.ToString());
                current.Clear();
                inPart = !trimmed.StartsWith(marker + "--", StringComparison.Ordinal);
                continue;
            }

            if (inPart) current.Append(trimmed).Append('\n');
        }

        if (inPart && current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    private static (string MediaType, Dictionary<string, string> Parameters) ParseContentType(string? value)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return ("text/plain", parameters);

        var segments = value.Split(';');
        var mediaType = segments[0].Trim().ToLowerInvariant();

        for (var i = 1; i < segments.Length; i++)
        {
            var equals = segments[i].IndexOf('=');
            if (equals <= 0) continue;
            var name = segments[i][..equals].Trim().ToLowerInvariant();
            var parameterValue = segments[i][(equals + 1)..].Trim().Trim('"');
            parameters[name] = parameterValue;
        }

        return (mediaType, parameters);
    }

    private static string? Parameter(string? headerValue, string name)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;

        foreach (var segment in headerValue.Split(';'))
        {
            var equals = segment.IndexOf('=');
            if (equals <= 0) continue;
            if (!segment[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return segment[(equals + 1)..].Trim().Trim('"');
        }

        return null;
    }

    private static string DecodeBody(string body, string transferEncoding, string? charset) =>
        transferEncoding.Trim().ToLowerInvariant() switch
        {
            "base64" => DecodeBytes(Convert.FromBase64String(StripWhitespace(body)), charset),
            "quoted-printable" => DecodeQuotedPrintable(body, charset),
            _ => DecodeBytes(GetEncoding(charset).GetBytes(body), charset),
        };

    private static string StripWhitespace(string value) =>
        new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static Encoding GetEncoding(string? charset) => (charset ?? "").Trim().ToLowerInvariant() switch
    {
        "iso-8859-1" or "latin1" or "latin-1" => Encoding.Latin1,
        "us-ascii" or "ascii" => Encoding.ASCII,
        _ => Encoding.UTF8,
    };

    private static string DecodeBytes(byte[] bytes, string? charset) =>
        GetEncoding(charset).GetString(bytes).Replace("\r\n", "\n");

    private static string DecodeQuotedPrintable(string body, string? charset)
    {
        var bytes = new List<byte>(body.Length);

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (c == '=')
            {
                // Soft line break: "=" immediately followed by a newline is dropped.
                if (i + 1 < body.Length && body[i + 1] == '\n') { i++; continue; }
                if (i + 2 < body.Length && body[i + 1] == '\r' && body[i + 2] == '\n') { i += 2; continue; }

                if (i + 2 < body.Length
                    && Uri.IsHexDigit(body[i + 1]) && Uri.IsHexDigit(body[i + 2]))
                {
                    bytes.Add(Convert.ToByte(body.Substring(i + 1, 2), 16));
                    i += 2;
                    continue;
                }
            }

            bytes.Add((byte)c);
        }

        return DecodeBytes([.. bytes], charset);
    }

    /// <summary>Decodes <c>=?utf-8?B?...?=</c> and <c>=?utf-8?Q?...?=</c> runs in a header.</summary>
    public static string DecodeWords(string value)
    {
        if (!value.Contains("=?", StringComparison.Ordinal)) return value;

        var result = new StringBuilder();
        var index = 0;

        while (index < value.Length)
        {
            var start = value.IndexOf("=?", index, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(value[index..]);
                break;
            }

            result.Append(value[index..start]);

            // =?charset?encoding?encoded-text?=
            var charsetEnd = value.IndexOf('?', start + 2);
            var encodingEnd = charsetEnd < 0 ? -1 : value.IndexOf('?', charsetEnd + 1);
            var textEnd = encodingEnd < 0 ? -1 : value.IndexOf("?=", encodingEnd + 1, StringComparison.Ordinal);

            if (charsetEnd < 0 || encodingEnd < 0 || textEnd < 0)
            {
                result.Append(value[start..]);
                break;
            }

            var charset = value[(start + 2)..charsetEnd];
            var encoding = value[(charsetEnd + 1)..encodingEnd].ToUpperInvariant();
            var encoded = value[(encodingEnd + 1)..textEnd];

            try
            {
                result.Append(encoding == "B"
                    ? DecodeBytes(Convert.FromBase64String(encoded), charset)
                    // Q encoding is quoted-printable with '_' standing for a space.
                    : DecodeQuotedPrintable(encoded.Replace('_', ' '), charset));
            }
            catch (FormatException)
            {
                result.Append(encoded);
            }

            index = textEnd + 2;
        }

        return result.ToString();
    }
}
