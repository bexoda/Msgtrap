namespace Msgtrap;

/// <summary>
/// Command line options. Deliberately tiny and dependency-free so the tool
/// starts instantly; unknown flags are ignored so embedding hosts (tests, IDEs)
/// can pass their own arguments.
/// </summary>
public sealed record AppOptions
{
    /// <summary>Interface to bind. Defaults to loopback so nothing leaves the machine.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Port serving every provider API and the web UI.</summary>
    public int Port { get; init; } = 8095;

    /// <summary>
    /// Providers to load, from <c>--provider</c>/<c>--only</c>. Empty means "all",
    /// which is the default: one port, every API, no configuration.
    /// </summary>
    public string[] Providers { get; init; } = [];

    /// <summary>Credential overrides parsed from <c>--credential provider.field=value</c>.</summary>
    public CredentialOverride[] CredentialOverrides { get; init; } = [];

    /// <summary>When true, providers reject requests whose credentials do not match.</summary>
    public bool RequireCredentials { get; init; }

    /// <summary>Interface for the SMTP listener. Loopback by default.</summary>
    public string SmtpHost { get; init; } = "127.0.0.1";

    /// <summary>Port for the SMTP listener. 1025 keeps clear of the privileged 25.</summary>
    public int SmtpPort { get; init; } = 1025;

    /// <summary>Set by <c>--no-smtp</c> to run the SMS side only.</summary>
    public bool EnableSmtp { get; init; } = true;

    /// <summary>How long a message stays <c>queued</c> before it flips to <c>delivered</c>.</summary>
    public TimeSpan DeliverAfter { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>SMS credits reported by balance endpoints. Always fake.</summary>
    public int SmsBalance { get; init; } = 2003;

    /// <summary>Main wallet balance reported as text, e.g. <c>GHS 20.99</c>.</summary>
    public string MainBalance { get; init; } = "GHS 20.99";

    /// <summary>Emit debug logs, including every HTTP request.</summary>
    public bool Verbose { get; init; }

    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }

    /// <summary>Base URL clients should use in place of the provider's host.</summary>
    public string BaseUrl => $"http://{Host}:{Port}";

    /// <summary>Reported by <c>--version</c> and in the startup banner.</summary>
    public const string Version = "0.4.0";

    /// <summary>A single <c>provider.field=value</c> credential override.</summary>
    public sealed record CredentialOverride(string ProviderId, string Field, string Value);

    /// <summary>Parses <c>--flag value</c> and <c>--flag</c> forms.</summary>
    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();
        var providers = new List<string>();
        var overrides = new List<CredentialOverride>();

        for (var i = 0; i < args.Length; i++)
        {
            string? Value() => i + 1 < args.Length ? args[++i] : null;

            switch (args[i])
            {
                case "-h" or "--help":
                    options = options with { ShowHelp = true };
                    break;
                case "-v" or "--version":
                    options = options with { ShowVersion = true };
                    break;
                case "--host" when Value() is { } host:
                    options = options with { Host = host };
                    break;
                case "-p" or "--port" when Value() is { } port && int.TryParse(port, out var parsedPort):
                    options = options with { Port = parsedPort };
                    break;
                // --provider and --only are the same thing; both accept a comma list.
                case "--provider" or "--only" when Value() is { } providerList:
                    providers.AddRange(providerList
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--credential" when Value() is { } assignment && ParseCredential(assignment) is { } parsed:
                    overrides.Add(parsed);
                    break;
                case "--require-credentials" or "--require-api-key":
                    options = options with { RequireCredentials = true };
                    break;
                // Sugar for --credential arkesel.api-key=... , kept for familiarity.
                case "--api-key" when Value() is { } apiKey:
                    overrides.Add(new CredentialOverride("arkesel", "api-key", apiKey));
                    break;
                case "--deliver-after" when Value() is { } ms && int.TryParse(ms, out var delay):
                    options = options with { DeliverAfter = TimeSpan.FromMilliseconds(Math.Max(0, delay)) };
                    break;
                case "--sms-balance" when Value() is { } balance && int.TryParse(balance, out var parsedBalance):
                    options = options with { SmsBalance = parsedBalance };
                    break;
                case "--main-balance" when Value() is { } main:
                    options = options with { MainBalance = main };
                    break;
                case "--verbose":
                    options = options with { Verbose = true };
                    break;
                case "--smtp-host" when Value() is { } smtpHost:
                    options = options with { SmtpHost = smtpHost };
                    break;
                case "--smtp-port" when Value() is { } smtpPort && int.TryParse(smtpPort, out var parsedSmtpPort):
                    options = options with { SmtpPort = parsedSmtpPort };
                    break;
                case "--no-smtp":
                    options = options with { EnableSmtp = false };
                    break;
            }
        }

        return options with { Providers = [.. providers], CredentialOverrides = [.. overrides] };
    }

    /// <summary>Splits <c>provider.field=value</c>; returns null when malformed.</summary>
    private static CredentialOverride? ParseCredential(string assignment)
    {
        var equals = assignment.IndexOf('=');
        var dot = assignment.IndexOf('.');
        if (equals <= 0 || dot <= 0 || dot > equals) return null;

        return new CredentialOverride(
            assignment[..dot].Trim().ToLowerInvariant(),
            assignment[(dot + 1)..equals].Trim().ToLowerInvariant(),
            assignment[(equals + 1)..]);
    }

    /// <summary>Text printed by <c>msgtrap --help</c>.</summary>
    public static string HelpText => $"""
        Msgtrap v{Version} - local SMS and email trap for development

        Usage: msgtrap [options]

        Providers:
          --provider <ids>         Serve only these providers, comma separated
                                   (e.g. --provider hubtel). Default: all of them.
          --credential <p.f=v>     Override a credential, e.g.
                                   --credential twilio.auth-token=abc
                                   Fields: arkesel.api-key, hubtel.client-id,
                                   hubtel.client-secret, twilio.account-sid,
                                   twilio.auth-token, mnotify.key
          --require-credentials    Reject requests with wrong credentials
          --api-key <key>          Shorthand for --credential arkesel.api-key=<key>

        Server:
          -p, --port <port>        Port for every SMS API and the web UI (default 8095)
              --host <host>        Interface to bind (default 127.0.0.1)
              --smtp-port <port>   SMTP port for email capture (default 1025)
              --smtp-host <host>   SMTP interface to bind (default 127.0.0.1)
              --no-smtp            Disable email capture entirely
              --deliver-after <ms> Simulated delivery delay (default 1500)
              --sms-balance <n>    Fake SMS credits for balance endpoints (default 2003)
              --main-balance <s>   Fake main balance (default "GHS 20.99")
              --verbose            Log every HTTP request and control-plane action

        Other:
          -v, --version            Print version and exit
          -h, --help               Print this help

        Run with no flags to serve every provider and configure them in the web UI.
        """;
}
