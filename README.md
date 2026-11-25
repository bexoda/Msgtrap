# Msgtrap

**A local inbox for development — every SMS provider API and SMTP email, captured on your
machine.**

Msgtrap runs on your machine and speaks several real SMS provider APIs plus plain SMTP. Point
your app's provider base URL or SMTP host at Msgtrap and every message is captured, inspected
and "delivered" to a simulated handset or mailbox instead of a real phone or inbox. No
credits, no carriers, no phone numbers and no email addresses leaving your laptop.

```
Your app ──SMS provider API──► Msgtrap :8095 ──► Web UI inbox ──► simulated delivery + callbacks
Your app ──SMTP─────────────► Msgtrap :1025 ──► Web UI inbox ──► HTML / text / headers / raw
```

## Why

- Testing OTP and notification flows burns SMS credits and juggles real SIM cards.
- Sending real email in tests means shared inboxes, deliverability and spam folders.
- Most providers have no local sandbox for their HTTP API.
- You want to see **exactly** the JSON your app receives, including error responses.
- Switching providers (or testing a library that supports several) shouldn't mean
  rewriting your test setup.

## Supported providers

| Provider | Endpoint | Auth style | Verified against |
| --- | --- | --- | --- |
| [Arkesel](docs/arkesel-coverage.md) | `POST /api/v2/sms/send` + OTP + balance | `api-key` header | Official OpenAPI spec |
| [Hubtel](docs/providers/hubtel.md) | `POST/GET /v1/messages/send` | Basic auth, or query credentials | Published docs + real integrations |
| [Twilio](docs/providers/twilio.md) | `POST /2010-04-01/Accounts/{Sid}/Messages.json` | Basic auth (SID + token) | Official API reference |
| [mNotify](docs/providers/mnotify.md) | `POST /api/sms/quick` + balance | `key` query parameter | Official OpenAPI spec |

All four listen on one port at their **native paths**, so nothing collides and there is no
config beyond a base URL. Want more? Adding a provider is one file plus one line — see
[CONTRIBUTING.md](CONTRIBUTING.md).

## Two channels, one inbox

| Channel | How your app reaches it | UI tab |
| --- | --- | --- |
| **SMS** | any provider's send API on `:8095` | `SMS` |
| **Email** | plain SMTP on `:1025` | `Email` |

Both land in the same web UI as separate tabs with live counts. Nothing is relayed, nothing
is delivered — Msgtrap is a trap, not a relay.

## Install

### Download a binary

Every [release](https://github.com/bexoda/Msgtrap/releases) publishes one self-contained
executable per platform — no .NET SDK or runtime needed:

| Platform | Asset |
| --- | --- |
| Windows x64 | `msgtrap-<version>-win-x64.zip` |
| Linux x64 | `msgtrap-<version>-linux-x64.tar.gz` |
| Linux arm64 | `msgtrap-<version>-linux-arm64.tar.gz` |
| macOS Intel | `msgtrap-<version>-osx-x64.tar.gz` |
| macOS Apple silicon | `msgtrap-<version>-osx-arm64.tar.gz` |

```bash
tar -xzf msgtrap-0.4.0-linux-x64.tar.gz
./msgtrap
```

The macOS binaries are unsigned, so if you downloaded one through a browser run
`xattr -d com.apple.quarantine msgtrap` first. The same `.nupkg` the tool is built from is
attached to each release if you prefer to install it from a local folder.

### Install as a .NET tool

Requires the .NET 10 SDK.

```bash
git clone https://github.com/<your-org>/Msgtrap.git
cd Msgtrap

dotnet pack src/Msgtrap/Msgtrap.csproj -c Release
dotnet tool install --global msgtrap --add-source ./artifacts
```

Now, from any directory:

```bash
msgtrap
```

```
Msgtrap v0.3.0  -  local SMS and email trap for development

  Web UI     http://127.0.0.1:8095
  Arkesel    POST http://127.0.0.1:8095/api/v2/sms/send
  Hubtel     POST http://127.0.0.1:8095/v1/messages/send
  Twilio     POST http://127.0.0.1:8095/2010-04-01/Accounts/{AccountSid}/Messages.json
  mNotify    POST http://127.0.0.1:8095/api/sms/quick?key=...
  SMTP       -    127.0.0.1:1025  (send mail here)

All providers loaded - switch them on or off in the web UI.
Messages and email live in memory only - nothing is sent, nothing is billed.
Press Ctrl+C (or Ctrl+X) to stop.
```

If port `1025` is busy, Msgtrap logs a warning and keeps running — SMS capture is unaffected.
Use `--smtp-port` to pick another port, or `--no-smtp` to skip email entirely.

Update after pulling changes:

```bash
dotnet pack src/Msgtrap/Msgtrap.csproj -c Release
dotnet tool update --global msgtrap --add-source ./artifacts
```

### Standalone binary (no .NET install)

Publishing with a runtime identifier gives you one self-contained `msgtrap` executable —
the Mailpit-style option: no SDK or runtime needed on the machine that runs it.

```bash
dotnet publish src/Msgtrap/Msgtrap.csproj -c Release -r win-x64
```

| RID | Output |
| --- | --- |
| `win-x64` | `artifacts/win-x64/msgtrap.exe` |
| `linux-x64` | `artifacts/linux-x64/msgtrap` |
| `linux-arm64` | `artifacts/linux-arm64/msgtrap` |
| `osx-x64` | `artifacts/osx-x64/msgtrap` |
| `osx-arm64` | `artifacts/osx-arm64/msgtrap` |

Cross-publishing for Linux and macOS works from any OS. The macOS binaries are unsigned,
so after copying one over run `chmod +x msgtrap` and, if it was downloaded through a
browser, `xattr -d com.apple.quarantine msgtrap`. In VS Code, Run Task → **publish exe (all)**
builds every target.

Run from source instead:

```bash
dotnet run --project src/Msgtrap -- --port 8095 --verbose
```

## Two ways to run it

**Serve everything, configure in the UI** (the default):

```bash
msgtrap
```

Every provider is loaded. Open <http://127.0.0.1:8095>, click **Providers**, and you get a
card per provider with its base URL, endpoint list, the fake credentials to paste into your
`.env`, and a switch to turn it off (its routes then answer `404`).

**Serve one provider:**

```bash
msgtrap --provider hubtel
```

Only Hubtel's routes are registered. `--provider hubtel,twilio` loads two. This is the
closest thing to "just run the provider my app uses".

There is no difference in behaviour beyond which routes exist — the inbox, delivery
simulation and UI are shared.

## Point your app at it

Only the host changes. Keep your real-looking key in the header/parameter; Msgtrap accepts
anything until you ask it to enforce credentials.

### Arkesel

```diff
- https://sms.arkesel.com/api/v2/sms/send
+ http://127.0.0.1:8095/api/v2/sms/send
```

```bash
curl -X POST http://127.0.0.1:8095/api/v2/sms/send \
  -H "api-key: msgtrap-dev-key" \
  -H "Content-Type: application/json" \
  -d '{"sender":"Arkesel","message":"Your code is 123456","recipients":["233544919953"]}'
```

### Hubtel

```bash
curl -X POST http://127.0.0.1:8095/v1/messages/send \
  -u "msgtrap-hubtel-client:msgtrap-hubtel-secret" \
  -H "Content-Type: application/json" \
  -d '{"from":"Msgtrap","to":"233544919953","content":"Your code is 123456"}'
```

The legacy query-string form works too:

```bash
curl "http://127.0.0.1:8095/v1/messages/send?ClientId=msgtrap-hubtel-client&ClientSecret=msgtrap-hubtel-secret&From=Msgtrap&To=233544919953&Content=hello"
```

### Twilio

```bash
curl -X POST "http://127.0.0.1:8095/2010-04-01/Accounts/AC00000000000000000000000000000000/Messages.json" \
  -u "AC00000000000000000000000000000000:msgtrap-twilio-token" \
  -d "To=+233544919953" -d "From=Msgtrap" -d "Body=Your code is 123456"
```

```json
{ "sid": "SM4f2c…", "status": "queued", "num_segments": "1", "direction": "outbound-api" }
```

### mNotify

```bash
curl -X POST "http://127.0.0.1:8095/api/sms/quick?key=msgtrap-mnotify-key" \
  -H "Content-Type: application/json" \
  -d '{"recipient":["0241234567"],"sender":"mNotify","message":"Your code is 123456","is_schedule":false,"schedule_date":""}'
```

```json
{ "status": "success", "code": "2000", "summary": { "total_sent": 1, "credit_used": 1 } }
```

### SDKs

Because the wire formats are real, the official SDKs work — only the base URL changes:

```csharp
// Twilio .NET SDK
var twilio = new TwilioRestClient("AC00000000000000000000000000000000", "msgtrap-twilio-token");
// point HttpClient at http://127.0.0.1:8095 in your test setup
```

If an SDK hard-codes the provider host, that provider's docs page lists the paths Msgtrap
serves so you can map them with a proxy.

## Capturing email

Point your app's SMTP settings at `127.0.0.1:1025`. No TLS, no credentials, no sender
verification — any `MAIL FROM` and any `RCPT TO` is accepted.

```python
# Python (smtplib)
with smtplib.SMTP("127.0.0.1", 1025) as smtp:
    smtp.send_message(message, from_addr="ada@example.com", to_addrs=["bob@example.com"])
```

```js
// Node (nodemailer)
const transport = nodemailer.createTransport({ host: "127.0.0.1", port: 1025, secure: false });
```

```csharp
// .NET (MailKit)
using var client = new SmtpClient();
await client.ConnectAsync("127.0.0.1", 1025, SecureSocketOptions.None);
await client.SendAsync(message);
```

```env
# Laravel / Django / anything env-driven
MAIL_HOST=127.0.0.1
MAIL_PORT=1025
MAIL_USERNAME=
MAIL_PASSWORD=
MAIL_ENCRYPTION=null
```

Each captured email shows:

- **From / To / Cc / Bcc** — Bcc is derived from the SMTP envelope, so it appears even
  though the header does not exist.
- **Subject** — RFC 2047 encoded words (`=?UTF-8?Q?...?=`, `=?UTF-8?B?...?=`) decoded.
- **HTML** part in a sandboxed frame (scripts cannot run), **Text** part, both when the
  message is `multipart/alternative`.
- **Headers** in order, duplicates preserved, folded lines unfolded.
- **Attachments** by filename, content type and size.
- **Raw source** exactly as transferred, if you need to diff what your mailer produced.
- **Size** and receive time.

Bodies decode `base64` and `quoted-printable`, with UTF-8, ISO-8859-1 and US-ASCII
charsets. See [docs/email.md](docs/email.md) for the full ESMTP behaviour and limits.

## Credentials

Every provider has deterministic fake credentials, shown in the **Providers** panel with
copy buttons:

| Provider | Fields | Default values |
| --- | --- | --- |
| Arkesel | `api-key` | `msgtrap-dev-key` |
| Hubtel | `client-id`, `client-secret` | `msgtrap-hubtel-client` / `msgtrap-hubtel-secret` |
| Twilio | `account-sid`, `auth-token` | `AC00000000000000000000000000000000` / `msgtrap-twilio-token` |
| mNotify | `key` | `msgtrap-mnotify-key` |

**Enforcement is off by default**, so nothing that works today breaks. Turn it on to test
your app's auth failure paths — each provider then answers with *its own* error shape
(Arkesel `403`, Hubtel `401`, Twilio `401` + code `20003`, mNotify `401` + code `1001`).

Override any value:

```bash
msgtrap --credential twilio.auth-token=my-test-token
msgtrap --credential hubtel.client-id=abc --credential hubtel.client-secret=def
msgtrap --api-key my-arkesel-key          # shorthand for --credential arkesel.api-key=...
```

## What every provider shares

Because all adapters feed one pipeline, these behave identically everywhere:

- **Invalid numbers** are rejected per number and reported in the provider's own error
  style, without failing the whole request.
- **Segments are counted like a carrier**: GSM-7 at 160/153 characters, UCS-2 at 70/67
  UTF-16 units, extension characters (`^{}\[~]|€`) at two septets.
- **Delivery is simulated**: a message is `queued`, then flips to `delivered` after
  `--deliver-after` ms (1.5 s by default), with a timeline in the UI.
- **Callbacks fire** in the provider's own convention (Arkesel `?sms_id=&status=`,
  Hubtel `GET ?MessageId=&Status=`, Twilio form POST with `MessageSid`/`MessageStatus`).
- **Domain-shaped output**: each adapter returns the envelope its SDK expects, including
  error codes.

## Arkesel OTP

Arkesel's OTP API is fully emulated — the generated code appears in the inbox so you read it
instead of a phone:

```bash
curl -X POST http://127.0.0.1:8095/api/otp/generate -H "api-key: msgtrap-dev-key" \
  -H "Content-Type: application/json" \
  -d '{"expiry":5,"length":6,"medium":"sms","message":"Your code is %otp_code%","number":"233544919953","sender_id":"Arkesel","type":"numeric"}'

curl -X POST http://127.0.0.1:8095/api/otp/verify -H "api-key: msgtrap-dev-key" \
  -H "Content-Type: application/json" \
  -d '{"code":"482913","number":"233544919953"}'
```

The UI shows the code in a copyable OTP card with an expiry countdown and a `verified`
badge. Full rules (latest-wins, single-use, expiry, attempt counting) are in
[docs/arkesel-coverage.md](docs/arkesel-coverage.md#post-apiotpgenerate).

OTP is Arkesel-only for now; Twilio Verify and the others have different flows and are not
implemented.

## The web UI

Open <http://127.0.0.1:8095>:

- **SMS / Email** in the sidebar with live counts over server-sent events
- A light sidebar app shell with the inbox list and reading pane side by side
- Live SMS inbox tagged with the provider that received it
- Search sender, body, number or id; filter by status **and provider**
- Message detail: recipients, message ids, encoding, segments, callback URL, delivery
  timeline, OTP panel when relevant, and the raw request body
- Email detail: HTML (sandboxed), Text, Headers and Raw source tabs, attachments and
  envelope recipients
- **Providers** panel: base URL, endpoints, credentials, per-provider on/off switch, and
  the SMTP address to point your mailer at
- **Compose** posts through the real Arkesel path so you can verify by hand
- **Settings**: credential enforcement, simulated failures, delivery delay, fake balance

All UI assets are embedded in the binary; no npm, no build step. The framework base is
vendored as `src/Msgtrap/wwwroot/vendor/bootstrap.min.css` and `bootstrap.bundle.min.js`,
and `styles.css` layers the admin-template look on top. They are served at the neutral URLs
`/vendor/base.css` and `/vendor/base.js` so the page does not advertise its stack, and the
`Server` response header is suppressed for the same reason.

Press **Ctrl+C** or **Ctrl+X** to stop the server.

## Options

| Flag | Default | Description |
| --- | --- | --- |
| `--provider <ids>` | all | Serve only these providers (`--provider hubtel,twilio`) |
| `--credential <p.f=v>` | — | Override a credential value; repeatable |
| `--require-credentials` | off | Reject requests whose credentials do not match |
| `--api-key <key>` | `msgtrap-dev-key` | Shorthand for `--credential arkesel.api-key=` |
| `-p`, `--port <port>` | `8095` | Port for every API and the web UI |
| `--host <host>` | `127.0.0.1` | Interface to bind |
| `--smtp-port <port>` | `1025` | SMTP port for email capture |
| `--smtp-host <host>` | `127.0.0.1` | SMTP interface to bind |
| `--no-smtp` | off | Disable email capture entirely |
| `--deliver-after <ms>` | `1500` | Simulated delivery delay |
| `--sms-balance <n>` | `2003` | Credits reported by balance endpoints |
| `--main-balance <s>` | `GHS 20.99` | Main balance reported by the balance endpoint |
| `--verbose` | off | Log every HTTP request and control-plane action |
| `-v`, `--version` | | Print the version |
| `-h`, `--help` | | Print help |

## Simulating failures

Arkesel supports full failure injection from the UI **Settings** panel or the control API:

```bash
curl -X PUT http://127.0.0.1:8095/api/control/settings \
  -H "Content-Type: application/json" \
  -d '{"forceError":"402"}'
```

| `forceError` | Status | Body message |
| --- | --- | --- |
| `402` | `402 Payment Required` | `Insufficient balance or invalid coverage!` |
| `403` | `403 Forbidden` | `Inactive SMS Gateway!` |
| `500` | `500 Internal Server Error` | `SMS request failed!` |
| `""` | — | Normal capture |

Failure injection is Arkesel-only today; the other adapters rely on credential enforcement
and their own validation errors. Adding it per provider is documented in
[CONTRIBUTING.md](CONTRIBUTING.md).

## Logging

```
09:41:02 info: Msgtrap.Core.MessageIngest[0] Captured twilio message 4f2c…: Msgtrap -> 1 recipient(s), 1xGSM-7 (22 chars)
09:41:04 info: Msgtrap.Core.MessageIngest[0] Delivered 4f2c… to 1 simulated handset(s) after 1500 ms
09:41:04 info: Msgtrap.Core.MessageIngest[0] Delivery callback http://localhost:3000/sms (SMS SM4f2c…) -> 200
09:41:09 info: Msgtrap.Core.SmtpServer[0] SMTP capture listening on 127.0.0.1:1025
09:41:21 info: Msgtrap.Core.SmtpServer[0] Captured email 8a1d… from Ada <ada@example.com> to 2 recipient(s), subject Welcome, 1187 bytes, 1 attachment(s)
```

Add `--verbose` for every HTTP request, the SSE lifecycle, listing filters and balance
lookups. Credentials are never logged.

## Architecture

```
src/Msgtrap
├── Program.cs              host, Kestrel, logging, CORS, request log middleware
├── AppOptions.cs           flag parsing and --help text
├── Core/                   provider-neutral pipeline
│   ├── MessageIngest.cs    the single place messages are created and delivered
│   ├── SmsAnalyzer.cs      GSM-7 / UCS-2 character and segment counting
│   ├── PhoneNumbers.cs     number normalization and validation
│   ├── ProviderCredentials.cs  expected values, enforcement, fake defaults
│   ├── RequestFields.cs    query/form/json helpers for adapters
│   ├── SmtpServer.cs       minimal ESMTP listener that traps email
│   ├── MimeParser.cs       RFC 5322 / MIME reader for captured mail
│   └── EventHub.cs         bounded pub/sub used by both stores
├── Providers/              one adapter per provider (the contribution surface)
│   ├── ISmsProvider.cs     the contract
│   ├── ArkeselProvider.cs  metadata for the Arkesel implementation in Arkesel/
│   ├── HubtelProvider.cs
│   ├── TwilioProvider.cs
│   ├── MnotifyProvider.cs
│   ├── ProviderRegistry.cs startup restriction + UI discovery
│   └── ProviderGuard.cs    runtime on/off
├── Arkesel/                Arkesel's send, balance and OTP endpoints
├── Control/                UI-only API: inbox, email, providers, settings, SSE
├── Store/                  in-memory messages, email, OTP registry, settings, pub/sub
└── wwwroot/                embedded single-page UI (no build step)
```

**The rule:** adapters translate. Only `Core/MessageIngest` creates messages, counts
segments, schedules delivery and fires callbacks. A provider that starts reimplementing
those is a bug.

## Development

```bash
dotnet build Msgtrap.sln -c Release
dotnet test  Msgtrap.sln -c Release
dotnet run   --project src/Msgtrap -- --verbose
```

Tests cover every adapter's success and error envelopes, credential enforcement per
provider, provider enable/disable, segment math, OTP expiry/single-use, MIME parsing
(encoded words, multipart, quoted-printable, attachments, Bcc derivation) and a real
socket-level SMTP conversation.

## Limitations

- In-memory only: restarting empties the inbox and forgets outstanding OTPs.
- HTTP and SMTP are bound to loopback by default and have no authentication. Do not expose
  them to a network you do not trust (Msgtrap prints a warning if you do).
- Browser safety: the inbox/settings API is same-origin only, requests with a non-loopback
  `Host` are refused (DNS rebinding), and the UI sends a strict CSP, so a website you visit
  cannot read your captured OTPs. Only the provider APIs allow cross-origin calls. HTML email
  previews are sandboxed and cannot load remote images.
- Delivery callbacks are real HTTP requests to whatever `callback` URL the sending app
  supplies (http/https only), so only point apps you trust at Msgtrap.
- Each adapter implements a documented **subset**; see the per-provider page for what is
  and is not emulated. Unsupported endpoints return `404`.
- Numbers are validated permissively (7–15 digits) because test numbers are usually fake.
- Scheduling records the intent but never delivers later.
- Email is parsed by a deliberately small MIME reader: no STARTTLS, no attachment content
  extraction (filename, type and size only), no `message/rfc822` recursion, and exotic
  charsets fall back to UTF-8. Messages over 10 MB are rejected.
- No persistence, no resume: intentional for a dev tool.

## Contributing

Adding a provider is the main contribution path. The checklist is in
[CONTRIBUTING.md](CONTRIBUTING.md#adding-a-provider), and each adapter is a good template —
`MnotifyProvider.cs` is the smallest.

## License

[MIT](LICENSE)
