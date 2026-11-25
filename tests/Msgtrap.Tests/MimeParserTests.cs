using Msgtrap.Core;
using Xunit;

namespace Msgtrap.Tests;

/// <summary>
/// Covers the MIME shapes real mailers actually produce: plain text, multipart
/// alternative with base64 parts, quoted-printable, RFC 2047 encoded words,
/// attachments and Bcc derived from the SMTP envelope.
/// </summary>
public sealed class MimeParserTests
{
    private static readonly DateTimeOffset Received = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_PlainTextMessage_ReadsHeadersAndBody()
    {
        const string raw =
            "From: Ada <ada@example.com>\r\n" +
            "To: bob@example.com\r\n" +
            "Subject: Hello\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            "Hi Bob,\r\nThis is a test.\r\n";

        var email = MimeParser.Parse(raw, ["bob@example.com"], "ada@example.com", Received);

        Assert.Equal("Ada <ada@example.com>", email.From);
        Assert.Equal(["bob@example.com"], email.To);
        Assert.Equal("Hello", email.Subject);
        Assert.Equal("Hi Bob,\nThis is a test.\n", email.TextBody);
        Assert.Equal("", email.HtmlBody);
        Assert.False(email.HasHtml);
        Assert.Empty(email.Bcc);
        Assert.Equal(Received, email.ReceivedAt);
    }

    [Fact]
    public void Parse_MultipartAlternative_KeepsTextAndHtml()
    {
        const string raw =
            "From: ada@example.com\r\n" +
            "To: bob@example.com\r\n" +
            "Subject: Both parts\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/alternative; boundary=\"BOUND\"\r\n" +
            "\r\n" +
            "--BOUND\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "UGxhaW4gYm9keQ==\r\n" +
            "--BOUND\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "PGI+SFRNTDwvYj4=\r\n" +
            "--BOUND--\r\n";

        var email = MimeParser.Parse(raw, ["bob@example.com"], "ada@example.com", Received);

        Assert.Equal("Plain body", email.TextBody.Trim());
        Assert.Equal("<b>HTML</b>", email.HtmlBody.Trim());
        Assert.True(email.HasHtml);
    }

    [Fact]
    public void Parse_QuotedPrintableBody_IsDecoded()
    {
        const string raw =
            "From: ada@example.com\r\n" +
            "Subject: QP\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n" +
            "\r\n" +
            "Caf=C3=A9 cr=C3=A8me\r\n";

        var email = MimeParser.Parse(raw, ["bob@example.com"], "ada@example.com", Received);

        Assert.Equal("Café crème", email.TextBody.Trim());
    }

    [Fact]
    public void Parse_EncodedWordSubject_IsDecoded()
    {
        const string raw =
            "From: =?UTF-8?B?QWRh?= <ada@example.com>\r\n" +
            "Subject: =?UTF-8?Q?Caf=C3=A9_order?=\r\n" +
            "\r\n" +
            "body\r\n";

        var email = MimeParser.Parse(raw, ["bob@example.com"], "ada@example.com", Received);

        Assert.Equal("Café order", email.Subject);
        Assert.Equal("Ada <ada@example.com>", email.From);
    }

    [Fact]
    public void Parse_FoldedHeader_IsUnfolded()
    {
        const string raw =
            "From: ada@example.com\r\n" +
            "Subject: A subject that was\r\n" +
            " wrapped by the client\r\n" +
            "\r\n" +
            "body\r\n";

        var email = MimeParser.Parse(raw, ["bob@example.com"], "ada@example.com", Received);

        Assert.Equal("A subject that was wrapped by the client", email.Subject);
    }

    [Fact]
    public void Parse_MixedWithAttachment_RecordsDescriptor()
    {
        const string raw =
            "From: ada@example.com\r\n" +
            "Subject: With attachment\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=BOUND\r\n" +
            "\r\n" +
            "--BOUND\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "See attached.\r\n" +
            "--BOUND\r\n" +
            "Content-Type: text/csv; name=\"report.csv\"\r\n" +
            "Content-Disposition: attachment; filename=\"report.csv\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "YSxiLGMK\r\n" +
            "--BOUND--\r\n";

        var email = MimeParser.Parse(raw, ["bob@example.com"], "ada@example.com", Received);

        Assert.Equal("See attached.", email.TextBody.Trim());
        var attachment = Assert.Single(email.Attachments);
        Assert.Equal("report.csv", attachment.FileName);
        Assert.Equal("text/csv", attachment.ContentType);
    }

    [Fact]
    public void Parse_RecipientOnlyInEnvelope_IsTreatedAsBcc()
    {
        const string raw =
            "From: ada@example.com\r\n" +
            "To: bob@example.com\r\n" +
            "Subject: Hidden copy\r\n" +
            "\r\n" +
            "secret\r\n";

        var email = MimeParser.Parse(
            raw, ["bob@example.com", "carol@example.com"], "ada@example.com", Received);

        Assert.Equal(["bob@example.com"], email.To);
        Assert.Equal(["carol@example.com"], email.Bcc);
    }

    [Fact]
    public void Parse_MissingFromHeader_FallsBackToEnvelopeSender()
    {
        const string raw = "Subject: No from\r\n\r\nbody\r\n";

        var email = MimeParser.Parse(raw, ["bob@example.com"], "envelope@example.com", Received);

        Assert.Equal("envelope@example.com", email.From);
        Assert.Equal("envelope@example.com", email.EnvelopeFrom);
    }

    [Fact]
    public void AddressList_WithQuotedNameContainingComma_KeepsOneEntry()
    {
        var list = MimeParser.AddressList("\"Doe, John\" <john@example.com>, jane@example.com");

        Assert.Equal(["\"Doe, John\" <john@example.com>", "jane@example.com"], list);
    }

    [Fact]
    public void ExtractAddress_WithDisplayName_ReturnsBareAddress()
    {
        Assert.Equal("john@example.com", MimeParser.ExtractAddress("\"Doe, John\" <john@example.com>"));
        Assert.Equal("john@example.com", MimeParser.ExtractAddress("  john@example.com  "));
    }
}
