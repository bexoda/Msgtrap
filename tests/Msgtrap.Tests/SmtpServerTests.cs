using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Msgtrap;
using Msgtrap.Core;
using Msgtrap.Store;
using Xunit;

namespace Msgtrap.Tests;

/// <summary>
/// Drives <see cref="SmtpServer"/> over a real socket, the way a mail client
/// does. Each test starts its own listener on an ephemeral port so parallel
/// runs never fight over 1025.
/// </summary>
public sealed class SmtpServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Session_WithStandardConversation_CapturesMessage()
    {
        await using var harness = await SmtpHarness.StartAsync();
        await using var client = await harness.ConnectAsync();

        Assert.StartsWith("220", await client.ReadReplyAsync());

        var ehlo = await client.CommandAsync("EHLO test.local");
        Assert.Contains("250-8BITMIME", ehlo);

        Assert.StartsWith("250", await client.CommandAsync("MAIL FROM:<ada@example.com>"));
        Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<bob@example.com>"));
        Assert.StartsWith("354", await client.CommandAsync("DATA"));
        Assert.StartsWith("250", await client.SendDataAsync(
            "From: Ada <ada@example.com>\n" +
            "To: bob@example.com\n" +
            "Subject: Hello over SMTP\n" +
            "Content-Type: text/plain; charset=utf-8\n" +
            "\n" +
            "Hello from the test."));

        var email = Assert.Single(harness.Store.List(new EmailFilter(null)));
        Assert.Equal("Hello over SMTP", email.Subject);
        Assert.Equal("Ada <ada@example.com>", email.From);
        Assert.Equal(["bob@example.com"], email.To);
        Assert.Contains("Hello from the test.", email.TextBody);
        Assert.Equal("ada@example.com", email.EnvelopeFrom);
    }

    [Fact]
    public async Task Data_WithDotStuffedLine_IsUnstuffed()
    {
        await using var harness = await SmtpHarness.StartAsync();
        await using var client = await harness.ConnectAsync();

        await client.ReadReplyAsync();
        await client.CommandAsync("HELO test.local");
        await client.CommandAsync("MAIL FROM:<ada@example.com>");
        await client.CommandAsync("RCPT TO:<bob@example.com>");
        await client.CommandAsync("DATA");
        await client.SendDataAsync(
            "Subject: Dots\n\n" +
            "..leading dots stay content\n" +
            "...three become two");

        var email = Assert.Single(harness.Store.List(new EmailFilter(null)));
        Assert.Contains(".leading dots stay content", email.TextBody);
        Assert.Contains("..three become two", email.TextBody);
    }

    [Fact]
    public async Task Auth_WithLoginMechanism_AcceptsAnyCredentials()
    {
        await using var harness = await SmtpHarness.StartAsync();
        await using var client = await harness.ConnectAsync();

        await client.ReadReplyAsync();
        await client.CommandAsync("EHLO test.local");

        Assert.StartsWith("334", await client.CommandAsync("AUTH LOGIN"));
        Assert.StartsWith("334", await client.CommandAsync("dXNlcg=="));
        Assert.StartsWith("235", await client.CommandAsync("cGFzcw=="));
    }

    [Fact]
    public async Task Auth_WithPlainMechanism_AcceptsInlineCredentials()
    {
        await using var harness = await SmtpHarness.StartAsync();
        await using var client = await harness.ConnectAsync();

        await client.ReadReplyAsync();
        await client.CommandAsync("EHLO test.local");

        Assert.StartsWith("235", await client.CommandAsync("AUTH PLAIN AGFkYUBleGFtcGxlLmNvbQBzZWNyZXQ="));
    }

    [Fact]
    public async Task Data_WithoutRecipient_IsRejected()
    {
        await using var harness = await SmtpHarness.StartAsync();
        await using var client = await harness.ConnectAsync();

        await client.ReadReplyAsync();
        await client.CommandAsync("EHLO test.local");
        await client.CommandAsync("MAIL FROM:<ada@example.com>");

        Assert.StartsWith("503", await client.CommandAsync("DATA"));
        Assert.Empty(harness.Store.List(new EmailFilter(null)));
    }

    [Fact]
    public async Task UnknownCommand_IsRejectedWithoutDroppingSession()
    {
        await using var harness = await SmtpHarness.StartAsync();
        await using var client = await harness.ConnectAsync();

        await client.ReadReplyAsync();
        Assert.StartsWith("502", await client.CommandAsync("STARTTLS"));
        Assert.StartsWith("250", await client.CommandAsync("NOOP"));
    }

    [Fact]
    public async Task Quit_EndsSession()
    {
        await using var harness = await SmtpHarness.StartAsync();
        await using var client = await harness.ConnectAsync();

        await client.ReadReplyAsync();
        Assert.StartsWith("221", await client.CommandAsync("QUIT"));
    }

    [Fact]
    public async Task StartAsync_WhenPortIsTaken_LeavesServerNotListening()
    {
        await using var harness = await SmtpHarness.StartAsync();

        await using var second = new SmtpServer(
            new EmailStore(),
            new AppOptions { SmtpPort = harness.Server.Port, SmtpHost = "127.0.0.1" },
            NullLogger<SmtpServer>.Instance);

        await second.StartAsync(Ct);

        Assert.False(second.IsListening);
        Assert.Contains("Cannot listen", second.DisabledReason);
    }

    [Fact]
    public async Task StartAsync_WithSmtpDisabled_DoesNotListen()
    {
        await using var server = new SmtpServer(
            new EmailStore(),
            new AppOptions { EnableSmtp = false },
            NullLogger<SmtpServer>.Instance);

        await server.StartAsync(Ct);

        Assert.False(server.IsListening);
        Assert.Contains("--no-smtp", server.DisabledReason);
    }
}

/// <summary>Owns one listener plus its store so a test can assert on captures.</summary>
internal sealed class SmtpHarness(SmtpServer server, EmailStore store) : IAsyncDisposable
{
    public SmtpServer Server { get; } = server;

    public EmailStore Store { get; } = store;

    public static async Task<SmtpHarness> StartAsync()
    {
        var store = new EmailStore();
        // Port 0 lets the OS pick a free port, so tests never collide.
        var server = new SmtpServer(
            store,
            new AppOptions { SmtpPort = 0, SmtpHost = "127.0.0.1" },
            NullLogger<SmtpServer>.Instance);

        await server.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(server.IsListening, "SMTP listener failed to start on an ephemeral port");

        return new SmtpHarness(server, store);
    }

    public async Task<TestSmtpClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", Server.Port, TestContext.Current.CancellationToken);
        return new TestSmtpClient(client);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync(TestContext.Current.CancellationToken);
        await Server.DisposeAsync();
    }
}

/// <summary>A minimal line-oriented SMTP client; enough for assertions.</summary>
internal sealed class TestSmtpClient(TcpClient client) : IAsyncDisposable
{
    private readonly StreamReader _reader = new(client.GetStream(), Encoding.Latin1);
    private readonly StreamWriter _writer = new(client.GetStream(), Encoding.Latin1) { NewLine = "\r\n", AutoFlush = true };

    /// <summary>Sends one command and reads the reply that follows.</summary>
    public async Task<string> CommandAsync(string line)
    {
        await _writer.WriteLineAsync(line);
        return await ReadReplyAsync();
    }

    /// <summary>Sends an RFC 5322 message as a DATA block and reads the reply.</summary>
    public async Task<string> SendDataAsync(string body)
    {
        // The terminator is CRLF "." CRLF on a line of its own.
        await _writer.WriteAsync(body.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n.\r\n");
        await _writer.FlushAsync();
        return await ReadReplyAsync();
    }

    /// <summary>
    /// Reads one (possibly multi-line) reply. Multi-line replies use
    /// <c>250-</c> for every line but the last, which uses <c>250 </c>.
    /// </summary>
    public async Task<string> ReadReplyAsync()
    {
        var builder = new StringBuilder();
        while (true)
        {
            var line = await _reader.ReadLineAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(line);
            builder.AppendLine(line);
            // A reply ends at the first line whose 4th character is a space.
            if (line.Length < 4 || line[3] == ' ') return builder.ToString().TrimEnd();
        }
    }

    public ValueTask DisposeAsync()
    {
        _writer.Dispose();
        _reader.Dispose();
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
