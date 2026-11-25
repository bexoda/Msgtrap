# Email capture over SMTP

Msgtrap listens for SMTP on `127.0.0.1:1025` by default and traps every message it receives.
Nothing is relayed and nothing leaves the machine.

```
Your app ──SMTP (:1025)──► Msgtrap ──► MimeParser ──► EmailStore ──► Web UI "Email" tab
```

## Point your app at it

| Setting | Value |
| --- | --- |
| Host | `127.0.0.1` |
| Port | `1025` (`--smtp-port` to change) |
| Encryption | **none** (`STARTTLS` is not advertised) |
| Username / password | anything, or nothing (`AUTH PLAIN`/`LOGIN` accept any value) |
| From address | anything goes — there is no sender verification |

```python
import smtplib
from email.message import EmailMessage

msg = EmailMessage()
msg["From"] = "Ada <ada@example.com>"
msg["To"] = "bob@example.com"
msg["Subject"] = "Hello"
msg.set_content("Plain text part")
msg.add_alternative("<h1>HTML part</h1>", subtype="html")

with smtplib.SMTP("127.0.0.1", 1025) as smtp:
    smtp.send_message(msg)
```

```js
// nodemailer
await nodemailer.createTransport({ host: "127.0.0.1", port: 1025, secure: false })
  .sendMail({ from: "ada@example.com", to: "bob@example.com", subject: "Hello", text: "Hi" });
```

```csharp
// MailKit
using var client = new SmtpClient();
await client.ConnectAsync("127.0.0.1", 1025, SecureSocketOptions.None);
await client.SendAsync(MimeMessage.CreateFromMailMessage(new MailMessage(
    "ada@example.com", "bob@example.com", "Hello", "Hi")));
```

```env
# Laravel, Django, Rails, anything driven by env vars
MAIL_HOST=127.0.0.1
MAIL_PORT=1025
MAIL_ENCRYPTION=null
```

## Supported ESMTP commands

| Command | Behaviour |
| --- | --- |
| `EHLO` | Advertises `8BITMIME`, `SIZE 10485760` and `AUTH PLAIN LOGIN` |
| `HELO` | Same, without extensions |
| `AUTH PLAIN` / `AUTH LOGIN` | Accepts any credentials, always `235 Authentication successful` |
| `MAIL FROM` | Records the envelope sender |
| `RCPT TO` | Records up to 100 recipients per message |
| `DATA` | Accepts up to 10 MB, undoes dot-stuffing, then parses and stores |
| `RSET`, `NOOP` | No-ops that keep the session alive |
| `VRFY`, `EXPN` | `252 Cannot verify user` |
| `QUIT` | `221 Bye` |
| anything else | `502 Command not implemented` — the session stays open |

`DATA` before any `RCPT TO` is rejected with `503`; an oversized message is drained and
rejected with `552`. Sessions are closed after two minutes of idleness.

## What is parsed

| Field | Notes |
| --- | --- |
| From, To, Cc | Decoded, including RFC 2047 encoded words in display names |
| **Bcc** | Derived: recipients accepted on the envelope but absent from `To`/`Cc` |
| Subject | Encoded words decoded (`B` and `Q`), folded lines unfolded |
| Text body | `text/plain`, `base64` or `quoted-printable` decoded |
| HTML body | `text/html`, rendered in a `sandbox` iframe with no scripts |
| Headers | Every top-level header, in order, duplicates preserved |
| Attachments | Filename, content type and size (contents are not extractable) |
| Raw source | Exactly what the client sent, after dot-unstuffing |
| Size, received at | Byte count and UTC timestamp |

Multipart containers handled: `alternative`, `mixed`, `related`.

Charsets: UTF-8, ISO-8859-1/Latin-1 and US-ASCII. Anything else is decoded as UTF-8.

## Control API

The UI talks to these routes; they are local-only and not part of any mail protocol.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/control/emails?q=&from=` | List captured email, newest first |
| `GET` | `/api/control/emails/{id}` | One email, including raw source |
| `DELETE` | `/api/control/emails/{id}` | Delete one email |
| `DELETE` | `/api/control/emails` | Empty the mailbox |
| `GET` | `/api/control/emails/events` | Server-sent events: `message`, `delete`, `clear` |
| `GET` | `/api/control/smtp` | Host, port and state of the SMTP listener |

## Failure behaviour

| Situation | What happens |
| --- | --- |
| SMTP port already in use | Warning logged, SMTP disabled, SMS capture keeps working |
| `--no-smtp` | No listener, `GET /api/control/smtp` reports `enabled: false` |
| Message over 10 MB | `552 Message too big`; the session survives |
| Malformed MIME | Parsed as best it can be, stored anyway — you still see the raw source |
| Client disconnects mid-session | Session dropped, nothing stored |

## Deliberate limits

- `STARTTLS` is never advertised, so clients configured for TLS fail fast instead of
  silently upgrading.
- Attachment **content** is not stored separately, only the descriptor.
- `message/rfc822` (attached emails) is not recursed into.
- No IMAP, no POP3: read mail in the web UI or through the control API.

Adding IMAP or attachment download is a welcome contribution — see
[CONTRIBUTING.md](../CONTRIBUTING.md).
