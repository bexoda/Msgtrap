using System.Net;
using System.Net.Sockets;
using System.Text;
using Msgtrap.Store;

namespace Msgtrap.Core;

/// <summary>
/// A small SMTP listener that captures everything it is given and never relays.
/// </summary>
/// <remarks>
/// Supports the subset real mailers use: EHLO/HELO, MAIL, RCPT, DATA, RSET, NOOP,
/// QUIT, SIZE, 8BITMIME and AUTH PLAIN/LOGIN (any credentials are accepted).
/// STARTTLS is intentionally not advertised, so clients never try it. Binding
/// failure is non-fatal: SMS capture keeps working and a warning is logged.
/// </remarks>
public sealed class SmtpServer(
    EmailStore store,
    AppOptions options,
    ILogger<SmtpServer> logger) : IHostedService, IAsyncDisposable
{
    private const int MaxMessageBytes = 10 * 1024 * 1024;
    private const int MaxRecipients = 100;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>The port actually bound, or 0 when SMTP capture is off.</summary>
    public int Port { get; private set; }

    /// <summary>True once the listener is accepting connections.</summary>
    public bool IsListening => _listener is not null;

    /// <summary>Why capture is off, for the UI; null while listening.</summary>
    public string? DisabledReason { get; private set; }

    /// <summary>Starts listening. A busy port disables SMTP capture instead of crashing.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.EnableSmtp)
        {
            DisabledReason = "Email capture is disabled (--no-smtp).";
            logger.LogInformation("SMTP capture disabled by --no-smtp");
            return Task.CompletedTask;
        }

        try
        {
            var address = IPAddress.TryParse(options.SmtpHost, out var parsed)
                ? parsed
                : IPAddress.Loopback;

            _listener = new TcpListener(address, options.SmtpPort);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        catch (SocketException ex)
        {
            DisabledReason = $"Cannot listen on {options.SmtpHost}:{options.SmtpPort} - {ex.Message}";
            logger.LogWarning(
                "SMTP capture disabled: cannot listen on {Host}:{Port} ({Message}). SMS capture is unaffected.",
                options.SmtpHost, options.SmtpPort, ex.Message);
            _listener = null;
            return Task.CompletedTask;
        }

        DisabledReason = null;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);

        logger.LogInformation("SMTP capture listening on {Host}:{Port}", options.SmtpHost, Port);
        return Task.CompletedTask;
    }

    /// <summary>Stops accepting connections and drops live sessions.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        _listener?.Stop();
        return ValueTask.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        using var stream = client.GetStream();
        // Latin1 keeps every byte intact; SMTP DATA is 8-bit transparent.
        using var reader = new StreamReader(stream, Encoding.Latin1, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.Latin1, 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        var envelopeFrom = "";
        var recipients = new List<string>();

        try
        {
            await writer.WriteLineAsync("220 msgtrap ESMTP ready");

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLineAsync(reader);
                if (line is null) break;

                var verb = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();

                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-msgtrap");
                        await writer.WriteLineAsync("250-8BITMIME");
                        await writer.WriteLineAsync($"250-SIZE {MaxMessageBytes}");
                        await writer.WriteLineAsync("250 AUTH PLAIN LOGIN");
                        break;
                    case "HELO":
                        await writer.WriteLineAsync("250 msgtrap");
                        break;
                    case "AUTH":
                        await HandleAuthAsync(line, reader, writer);
                        break;
                    case "MAIL":
                        envelopeFrom = ExtractPath(line);
                        recipients.Clear();
                        await writer.WriteLineAsync("250 2.1.0 Ok");
                        break;
                    case "RCPT":
                        if (recipients.Count >= MaxRecipients)
                        {
                            await writer.WriteLineAsync("452 4.5.3 Too many recipients");
                            break;
                        }
                        recipients.Add(ExtractPath(line));
                        await writer.WriteLineAsync("250 2.1.5 Ok");
                        break;
                    case "DATA":
                        if (recipients.Count == 0)
                        {
                            await writer.WriteLineAsync("503 5.5.1 Need RCPT before DATA");
                            break;
                        }
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var (source, tooLarge) = await ReadDataAsync(reader);
                        if (tooLarge)
                        {
                            await writer.WriteLineAsync("552 5.3.4 Message too big");
                            break;
                        }

                        var email = MimeParser.Parse(source, recipients, envelopeFrom, DateTimeOffset.UtcNow);
                        store.Add(email);
                        logger.LogInformation(
                            "Captured email {EmailId} from {From} to {Recipients} recipient(s), subject {Subject}, {Size} bytes, {Attachments} attachment(s)",
                            email.Id, email.From, email.EnvelopeTo.Count,
                            email.Subject.Length == 0 ? "(none)" : email.Subject,
                            email.SizeBytes, email.Attachments.Count);

                        await writer.WriteLineAsync($"250 2.0.0 Ok: queued as {email.Id}");
                        envelopeFrom = "";
                        recipients.Clear();
                        break;
                    case "RSET":
                        envelopeFrom = "";
                        recipients.Clear();
                        await writer.WriteLineAsync("250 2.0.0 Ok");
                        break;
                    case "NOOP":
                        await writer.WriteLineAsync("250 2.0.0 Ok");
                        break;
                    case "VRFY":
                    case "EXPN":
                        await writer.WriteLineAsync("252 2.5.2 Cannot verify user");
                        break;
                    case "HELP":
                        await writer.WriteLineAsync("214 2.0.0 See https://github.com for SMTP basics");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;
                    case "STAR":
                        // STARTTLS is deliberately not offered.
                        await writer.WriteLineAsync("502 5.5.1 Command not implemented");
                        break;
                    default:
                        await writer.WriteLineAsync("502 5.5.1 Command not implemented");
                        break;
                }
            }
        }
        catch (IOException)
        {
            // client vanished mid-session
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogWarning("SMTP session failed: {Message}", ex.Message);
        }
    }

    private static async Task HandleAuthAsync(string line, StreamReader reader, StreamWriter writer)
    {
        var mechanism = line.Length > 5 ? line[5..].Trim().ToUpperInvariant() : "";

        if (mechanism.StartsWith("LOGIN", StringComparison.Ordinal))
        {
            await writer.WriteLineAsync("334 VXNlcm5hbWU6"); // "Username:"
            await ReadLineAsync(reader);
            await writer.WriteLineAsync("334 UGFzc3dvcmQ6"); // "Password:"
            await ReadLineAsync(reader);
            await writer.WriteLineAsync("235 2.7.0 Authentication successful");
            return;
        }

        if (mechanism.StartsWith("PLAIN", StringComparison.Ordinal))
        {
            // "AUTH PLAIN <base64>" carries the credentials inline; without them,
            // send an empty challenge and accept whatever comes back.
            if (mechanism.Trim().Length == "PLAIN".Length)
            {
                await writer.WriteLineAsync("334 ");
                await ReadLineAsync(reader);
            }
            await writer.WriteLineAsync("235 2.7.0 Authentication successful");
            return;
        }

        await writer.WriteLineAsync("504 5.5.4 Unrecognized authentication type");
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(IdleTimeout);
        try
        {
            return await reader.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Reads the DATA block until the lone "." line, undoing dot-stuffing.</summary>
    private static async Task<(string Source, bool TooLarge)> ReadDataAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var tooLarge = false;

        while (true)
        {
            var line = await ReadLineAsync(reader);
            if (line is null) break;
            if (line == ".") break;

            // Transparency: a leading dot is doubled by the sender.
            if (line.StartsWith("..", StringComparison.Ordinal)) line = line[1..];

            builder.Append(line).Append("\r\n");
            if (builder.Length > MaxMessageBytes)
            {
                tooLarge = true;
                // Keep draining so the client sees a clean error rather than a reset.
                while (line is not null && line != ".")
                    line = await ReadLineAsync(reader);
                break;
            }
        }

        return (builder.ToString(), tooLarge);
    }

    /// <summary>Extracts <c>addr@host</c> from <c>MAIL FROM:&lt;addr@host&gt;</c>.</summary>
    private static string ExtractPath(string line)
    {
        var open = line.IndexOf('<');
        var close = line.LastIndexOf('>');
        if (open >= 0 && close > open) return line[(open + 1)..close].Trim();

        var colon = line.IndexOf(':');
        return colon >= 0 ? line[(colon + 1)..].Trim() : "";
    }
}
