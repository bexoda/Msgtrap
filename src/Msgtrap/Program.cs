using System.Diagnostics;
using System.Text;
using Msgtrap;
using Msgtrap.Arkesel;
using Msgtrap.Control;
using Msgtrap.Core;
using Msgtrap.Providers;
using Msgtrap.Store;
using Msgtrap.Ui;

var options = AppOptions.Parse(args);

if (options.ShowHelp)
{
    Console.WriteLine(AppOptions.HelpText);
    return;
}
if (options.ShowVersion)
{
    Console.WriteLine($"msgtrap {AppOptions.Version}");
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});
// Framework chatter stays off; Msgtrap logs captures, deliveries, simulated
// errors and settings changes at Information (Debug with --verbose).
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Msgtrap", options.Verbose ? LogLevel.Debug : LogLevel.Information);

builder.WebHost.UseUrls(options.BaseUrl);

// Do not advertise the server stack in a response header.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

// Credentials start from the fake defaults, then any --credential overrides win.
var credentials = DefaultCredentials.Create();
foreach (var over in options.CredentialOverrides)
{
    if (!credentials.TryGetValue(over.ProviderId, out var fields))
    {
        Console.Error.WriteLine($"msgtrap: unknown provider '{over.ProviderId}' in --credential {over.ProviderId}.{over.Field}");
        Environment.ExitCode = 2;
        return;
    }
    fields[over.Field] = over.Value;
}

var store = new MessageStore(new SimulatorSettings
{
    EnforceCredentials = options.RequireCredentials,
    Credentials = credentials,
    DeliverAfterMs = (int)options.DeliverAfter.TotalMilliseconds,
    SmsBalance = options.SmsBalance,
    MainBalance = options.MainBalance,
});

var otps = new OtpStore();
var emails = new EmailStore();

builder.Services.AddSingleton(store);
builder.Services.AddSingleton(otps);
builder.Services.AddSingleton(emails);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(5) });

// Shared pipeline every provider feeds.
builder.Services.AddSingleton<MessageIngest>();
builder.Services.AddSingleton<ProviderCredentials>();

// Providers. Adding one is a single line here plus a file under Providers/.
builder.Services.AddSingleton<ArkeselEndpoints>();
builder.Services.AddSingleton<OtpEndpoints>();
builder.Services.AddSingleton<ISmsProvider, ArkeselProvider>();
builder.Services.AddSingleton<ISmsProvider, HubtelProvider>();
builder.Services.AddSingleton<ISmsProvider, TwilioProvider>();
builder.Services.AddSingleton<ISmsProvider, MnotifyProvider>();

builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<ControlEndpoints>();

// Email capture: a plain SMTP listener feeding its own in-memory store.
builder.Services.AddSingleton<SmtpServer>();
builder.Services.AddHostedService(services => services.GetRequiredService<SmtpServer>());
builder.Services.AddSingleton<SmtpStatus>();
builder.Services.AddSingleton<EmailEndpoints>();

var app = builder.Build();

ProviderRegistry registry;
try
{
    registry = app.Services.GetRequiredService<ProviderRegistry>();
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"msgtrap: {ex.Message}");
    Environment.ExitCode = 2;
    return;
}

var httpLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Msgtrap.Http");

// Browser-facing guard. The inbox holds OTPs and reset links, so a web page the developer
// happens to visit must not be able to read or change it:
//  - Host check: stops DNS rebinding (evil.com re-resolving to 127.0.0.1). Only enforced when
//    bound to loopback; binding elsewhere is an explicit opt-in to being reachable.
//  - Origin check: control plane and UI are same-origin only (no CORS headers at all).
//  - Wildcard CORS stays on the provider APIs only, because a dev web app may call them directly.
var loopbackBound = IsLoopback(options.Host);
app.Use(async (context, next) =>
{
    var request = context.Request;
    var headers = context.Response.Headers;

    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    // Email previews are untrusted HTML: no remote images/fonts/scripts means no tracking pixels.
    headers["Content-Security-Policy"] =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    if (loopbackBound && !IsLoopback(request.Host.Host))
    {
        context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        return;
    }

    var path = request.Path.Value ?? "/";
    // Routing is case-insensitive, so this must be too (/API/Control/... still hits the control plane).
    var isControl = path.Equals("/api/control", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/control/", StringComparison.OrdinalIgnoreCase);
    var isProviderApi = !isControl
                        && (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                            || path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)
                            || path.StartsWith("/2010-04-01/", StringComparison.OrdinalIgnoreCase));

    if (isProviderApi)
    {
        headers["Access-Control-Allow-Origin"] = "*";
        headers["Access-Control-Allow-Headers"] = "Content-Type, api-key, x-api-key, Authorization";
        headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
        if (HttpMethods.IsOptions(request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
    }
    else if (request.Headers.Origin.Count > 0
             && !(Uri.TryCreate(request.Headers.Origin[0], UriKind.Absolute, out var origin)
                  && string.Equals(origin.Authority, request.Host.ToString(), StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    else if (HttpMethods.IsOptions(request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return;
    }

    await next();
});

// One line per API call. Successful calls stay at Debug so the console remains
// readable; client errors are always logged because they usually explain a bug.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    var isProviderApi = path.StartsWith("/api/", StringComparison.Ordinal)
                        || path.StartsWith("/v1/", StringComparison.Ordinal)
                        || path.StartsWith("/2010-04-01/", StringComparison.Ordinal);
    if (!isProviderApi)
    {
        await next();
        return;
    }

    if (path == "/api/control/events")
    {
        httpLog.LogDebug("GET /api/control/events (live stream opened)");
        await next();
        return;
    }

    var stopwatch = Stopwatch.StartNew();
    await next();
    stopwatch.Stop();

    var level = context.Response.StatusCode >= StatusCodes.Status400BadRequest
        ? LogLevel.Warning
        : LogLevel.Debug;
    httpLog.Log(level, "{Method} {Path} -> {StatusCode} in {ElapsedMs} ms",
        context.Request.Method, path, context.Response.StatusCode, stopwatch.ElapsedMilliseconds);
});

foreach (var provider in registry.Loaded)
    provider.Map(app);

app.Services.GetRequiredService<ControlEndpoints>().Map(app);
app.Services.GetRequiredService<EmailEndpoints>().Map(app);
EmbeddedUi.Map(app);

try
{
    // Start first so the banner can report the SMTP port actually bound (which
    // differs from the request when port 0 asks the OS to pick one).
    await app.StartAsync();
}
catch (IOException ex)
{
    Console.Error.WriteLine($"msgtrap: cannot listen on {options.BaseUrl} - {ex.Message}");
    Console.Error.WriteLine("        Is another instance running? Try: msgtrap --port 8096");
    Environment.ExitCode = 1;
    return;
}

var smtp = app.Services.GetRequiredService<SmtpServer>();
var banner = new StringBuilder();
banner.AppendLine();
banner.AppendLine($"  Msgtrap v{AppOptions.Version}  -  local SMS and email trap for development");
banner.AppendLine();
banner.AppendLine($"    Web UI      {options.BaseUrl}");
foreach (var provider in registry.Loaded)
{
    var first = provider.Endpoints[0];
    banner.AppendLine($"    {provider.DisplayName,-10}  {first.Method,-4}  {options.BaseUrl}{first.Path}");
}
banner.AppendLine(smtp.IsListening
    ? string.Format("    {0,-10}  {1,-4}  {2}:{3}  (point your mailer here)",
        "SMTP", "-", options.SmtpHost, smtp.Port)
    : "    SMTP        (disabled)");
banner.AppendLine();
banner.AppendLine(registry.StartupRestricted
    ? "  Only the providers listed above are loaded (--provider)."
    : "  All providers loaded - switch them on or off in the web UI.");
if (!IsLoopback(options.Host) || (options.EnableSmtp && !IsLoopback(options.SmtpHost)))
    banner.AppendLine("  WARNING: bound beyond loopback. The inbox, settings and SMTP have no authentication;"
        + Environment.NewLine + "           anyone who can reach this port can read your captured OTPs and email."
        + Environment.NewLine);
banner.AppendLine("  Messages and email live in memory only - nothing is sent, nothing is billed.");
banner.AppendLine("  Press Ctrl+C (or Ctrl+X) to stop.");
banner.AppendLine();

Console.Write(banner.ToString());

// Ctrl+X never reaches a console control event, so watching raw keys is the only way
// to honour it. Skipped when stdin is redirected, where there are no keys to read.
if (!Console.IsInputRedirected)
    _ = WatchQuitKeyAsync(app.Lifetime);

await app.WaitForShutdownAsync();

static bool IsLoopback(string host) =>
    host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
    || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip));

// Ctrl+C is handled by the host; Ctrl+X needs a key loop.
static async Task WatchQuitKeyAsync(IHostApplicationLifetime lifetime)
{
    try
    {
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                if (QuitKeys.IsQuit(key))
                {
                    lifetime.StopApplication();
                    return;
                }
            }
            else
            {
                await Task.Delay(120, lifetime.ApplicationStopping).ConfigureAwait(false);
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Normal stop.
    }
    catch (InvalidOperationException)
    {
        // No console attached: nothing to watch.
    }
}

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
