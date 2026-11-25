# Contributing to Msgtrap

Thanks for helping out. Msgtrap is a small, dependency-light tool; keeping it that way is
part of the design.

## Getting started

```bash
dotnet build Msgtrap.sln -c Release
dotnet test  Msgtrap.sln -c Release
dotnet run   --project src/Msgtrap -- --verbose
```

Open <http://127.0.0.1:8095>, send a message from **Compose**, confirm it appears in the
SMS tab and transitions to `delivered`. Then send an email to `127.0.0.1:1025` (see
[Testing email by hand](#testing-email-by-hand)) and confirm it appears in the **Email** tab.

## Project layout

| Path | Purpose |
| --- | --- |
| `src/Msgtrap/Program.cs` | Host setup: logging, CORS, request logging, provider registration |
| `src/Msgtrap/AppOptions.cs` | Command line parsing and help text |
| `src/Msgtrap/Core/` | Provider-neutral pipeline: ingest, segments, numbers, credentials, SMTP + MIME |
| `src/Msgtrap/Providers/` | One adapter per provider — **the contribution surface** |
| `src/Msgtrap/Arkesel/` | Arkesel's endpoints (the reference implementation) |
| `src/Msgtrap/Control/` | UI-only API: inbox, email, providers, settings, SSE |
| `src/Msgtrap/Store/` | In-memory messages, email, OTP registry, settings, pub/sub |
| `src/Msgtrap/wwwroot/` | Embedded single-page UI (plain HTML/CSS/JS) |
| `tests/Msgtrap.Tests/` | Contract, provider, analyser, MIME and SMTP tests |

## Ground rules

- **No new runtime dependencies.** Only the shared ASP.NET Core framework; the UI is
  embedded. A `dotnet tool install` stays small and offline-friendly.
- **Adapters translate, they don't implement SMS.** Only `Core/MessageIngest` creates
  messages, counts segments, schedules delivery and fires callbacks. If you find yourself
  writing a `Dictionary` of statuses or a `Task.Delay`, stop and check `MessageIngest`.
- **Fidelity beats convenience.** Copy the provider's documented field names, casing,
  content type and status codes — even the ugly ones (`num_segments` as a string). A trap
  that lies is worse than no trap.
- **Label invented behaviour.** If the provider doesn't document an error body, say so in
  the provider page under `docs/providers/`. Never imply a guess is official.
- **Log what a developer needs.** Captures, deliveries, callbacks and rejections are
  `Information`; per-request traffic is `Debug`. Never log credentials.
- **XML-document public types.** The build treats warnings as errors.

## Adding a provider

Roughly an hour of work, most of it reading the provider's docs.

1. **Collect the facts.** Find the provider's API docs — ideally an OpenAPI spec (Arkesel
   and mNotify publish one). Write down the send endpoint, auth style, field names, content
   type, success envelope and at least one error envelope. Note which of these you had to
   guess: those go in the provider page as **Msgtrap-defined**.

2. **Create `src/Msgtrap/Providers/<Name>Provider.cs`.** Implement `ISmsProvider`
   (`Id`, `DisplayName`, `Summary`, `DocsUrl`, `Endpoints`, `Credentials`, `Map`).
   Use `MnotifyProvider.cs` as the smallest template and `TwilioProvider.cs` as the most
   complete (error codes, callback shaping).

   Your handler should:
   - check `ProviderGuard.IsDisabled(store, Id)` and return `ProviderGuard.Disabled(Id)`;
   - validate credentials with `credentials.Allows(...)` / `AllowsAll(...)`;
   - parse the provider's own request format into an `InboundSms`;
   - call `ingest.Capture(...)` **once**;
   - shape the provider's success response from `IngestResult`;
   - expose a `Func<string, Recipient, Task>` for delivery notifications.

3. **Register it.** Two lines in `Program.cs`:

   ```csharp
   builder.Services.AddSingleton<ISmsProvider, YourProvider>();
   ```

   and add its fake credentials to `Core/DefaultCredentials.Create()`.

4. **Document it.** `docs/providers/<provider>.md` with endpoints, auth, field table,
   success response, error table, callback convention, "not implemented" list — and a
   fidelity label per section. Link it from `docs/providers/README.md` and the root README.

5. **Test it.** Add a class in `tests/Msgtrap.Tests/` modelled on `ProviderApiTests.cs`:
   - success path asserts the exact envelope your docs promise;
   - one validation error;
   - one auth error with enforcement on;
   - callback fires (if you implemented one);
   - the message is tagged with your provider id.

   Start each test by resetting settings and clearing the inbox — the store is shared.

6. **Verify by hand** against the packaged tool:

   ```bash
   dotnet pack src/Msgtrap/Msgtrap.csproj -c Release
   dotnet tool update --global msgtrap --add-source ./artifacts
   msgtrap --provider yourprovider
   ```

Then, with enforcement on, confirm a wrong key gets the provider's own error shape.

## Changing an existing provider

Treat adapter changes like API changes: the provider's published contract is the spec.
Changing a field name or status code is only correct with a link to the documentation, and
must update the matching `docs/providers/*.md` in the same PR.

## Changing the email channel

Email is shared code, not an adapter. `Core/SmtpServer.cs` owns the protocol, `Core/MimeParser.cs`
owns parsing, `Store/EmailStore.cs` owns storage and `Control/EmailEndpoints.cs` owns the UI API.
Extend the parser rather than adding libraries, keep unsupported MIME cases documented in
[docs/email.md](docs/email.md), and keep the listener non-fatal when its port is taken.

## Tests

- `ArkeselApiTests` — Arkesel send/balance wire format, including simulated failures.
- `OtpApiTests` — OTP generate/verify contract, reading the code back from the inbox.
- `OtpStoreTests` — expiry, single-use and resend semantics with an injected clock.
- `ProviderApiTests` — Hubtel, Twilio, mNotify, the provider registry and enable/disable.
- `SmsAnalyzerTests` — segment math (160/153 GSM-7, 70/67 UCS-2, extension characters).
- `MimeParserTests` — plain text, multipart, base64, quoted-printable, encoded words,
  attachments and Bcc derivation.
- `SmtpServerTests` — a real socket conversation against a listener on an ephemeral port.

Add a test for every behaviour change. A new endpoint needs a success case, a validation
case and an error case.

### Testing email by hand

```bash
# in one terminal
msgtrap

# in another - Python 3 has smtplib built in
python -c "import smtplib,email.message as m; msg=m.EmailMessage(); msg['From']='a@example.com'; msg['To']='b@example.com'; msg['Subject']='hi'; msg.set_content('hello'); smtplib.SMTP('127.0.0.1',1025).send_message(msg)"
```

The message appears in the **Email** tab. If port 1025 is taken, pass `--smtp-port 2525`.

## UI changes

No build step: edit `src/Msgtrap/wwwroot/{index.html,app.js,styles.css}` and reload. Keep it
accessible (labels, focus styles, `Enter`/`Space` on rows) and respect
`prefers-reduced-motion`. Assets are embedded, so no project file changes are needed.

## Pull requests

1. One focused change per PR.
2. `dotnet test Msgtrap.sln` is green and `dotnet build Msgtrap.sln -c Release` has zero warnings.
3. Docs updated when behaviour, flags or providers change.

By contributing you agree your work is licensed under the [MIT License](../LICENSE).
