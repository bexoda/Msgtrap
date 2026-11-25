# Arkesel API coverage

Msgtrap mirrors the parts of the Arkesel **SMS API v2** that applications use when sending
transactional SMS. This document records exactly what is and is not emulated, so nobody is
surprised when an endpoint is missing.

Reference: Arkesel's published OpenAPI spec (`https://developers.arkesel.com/spec/api_spec.v2.4.0.yaml`).

## Endpoints

| Endpoint | Method | Msgtrap | Notes |
| --- | --- | --- | --- |
| `/api/v2/sms/send` | `POST` | Yes | Full capture, including scheduled, sandbox and callback variants |
| `/api/v2/clients/balance-details` | `GET` | Yes | Fake, configurable balance |
| `/api/otp/generate` | `POST` | Yes | Numeric/alphanumeric codes over sms/voice |
| `/api/otp/verify` | `POST` | Yes | Expiry, single-use and wrong-code handling |
| `/api/v2/sms` | `GET` | No | SMS history listing |
| `/api/v2/sms/message-reports` | `GET` | No | Delivery reports |
| `/api/v2/sms/template/send` | `POST` | No | Template sends |
| `/api/v2/sms/send/contact-group` | `POST` | No | Contact-group sends |
| `/api/v2/sms/voice/send` | `POST` | No | Voice API |
| `/sms/api?action=send-sms` | `GET` | No | Legacy v1 API |
| `/sms/api?action=check-balance` | `GET` | No | Legacy v1 balance |
| Contacts, webhooks, sender IDs, USSD, surveys, WhatsApp | — | No | Outside the scope of an SMS trap |

## Authentication

Arkesel uses an `api-key` request header for v2.

- Default: **any key is accepted**, so existing test fixtures keep working.
- With `--require-api-key` (or the UI switch), a missing or different key returns
  `403` with `{"status":"error","message":"Invalid API Key"}`.
- `x-api-key` is also accepted as an alias, because several SDK wrappers send that header.

## `POST /api/v2/sms/send`

### Request body

| Field | Type | Emulated behaviour |
| --- | --- | --- |
| `sender` | string | Required. Longer than 11 characters returns `422`. Stored verbatim. |
| `message` | string | Required. Empty/whitespace returns `422`. |
| `recipients` | string[] | Required, at least one entry. Each is normalized (spaces, `-`, `(`, `)`, `+` removed). |
| `scheduled_date` | string | Optional. Stored; response uses the scheduled envelope; the message is never delivered. |
| `callback_url` | string | Optional. Must be absolute `http(s)`; called on simulated delivery. |
| `sandbox` | bool | Optional. Stored and labelled in the UI. |
| `use_case` | string | Optional. Ignored beyond being stored in the raw request. |

### Success response

```json
{
  "status": "success",
  "data": [
    { "recipient": "233544919953", "id": "9b752841-7ee7-4d40-b4fe-768bfb1da4f0" },
    { "invalid numbers": ["22354674948"] }
  ]
}
```

- One `data` entry per valid recipient, in request order.
- `id` is a UUID v4, the same shape Arkesel returns.
- Unusable numbers are grouped into a single trailing `{"invalid numbers": [...]}` object
  instead of failing the request.

### Scheduled response

```json
{ "status": "success", "message": "SMS request sent successfully!" }
```

### Error responses

All errors use `{"status":"error","message":"..."}`.

| Status | When | Message |
| --- | --- | --- |
| `422` | `message` missing/empty | `The message field is required.` |
| `422` | `sender` missing/empty | `The sender field is required.` |
| `422` | `recipients` missing/empty | `The recipients field is required.` |
| `422` | `sender` > 11 characters | `The sender field must not be greater than 11 characters.` |
| `422` | body is not valid JSON | `The message field is required.` |
| `403` | key enforcement on, key missing/wrong | `Invalid API Key` |
| `402` | `forceError` = `402` | `Insufficient balance or invalid coverage!` |
| `403` | `forceError` = `403` | `Inactive SMS Gateway!` |
| `500` | `forceError` = `500` | `SMS request failed!` |

Errors raised by `forceError` happen **before** capture, so the inbox only contains
messages the API actually accepted.

### Delivery simulation

```
queued ──(deliver-after ms)──► delivered
```

- `--deliver-after` defaults to 1500 ms; `0` makes delivery immediate.
- Each recipient starts `queued` and flips to `delivered` together.
- The UI timeline records "Accepted by Msgtrap" and "Delivered to simulated handset".

### Callback (`callback_url`)

After delivery Msgtrap performs, once per recipient:

```
POST <callback_url>?sms_id=<recipient id>&status=delivered
Content-Type: application/json
Body: {}
```

- 5 second timeout, failures are logged and never retried.
- Non-`http(s)` or relative URLs are ignored with a warning.

## `GET /api/v2/clients/balance-details`

```json
{
  "status": "success",
  "data": { "sms_balance": "2003", "main_balance": "GHS 20.99" }
}
```

Values come from `--sms-balance` / `--main-balance` and can be changed at runtime from the
UI settings panel. When `forceError` is `500`, the endpoint returns Arkesel's
`Failed to obtain your balance details!` error.

## `POST /api/otp/generate`

Generates a code and captures it as a message, so the code is readable in the inbox.

### Request body

| Field | Type | Emulated behaviour |
| --- | --- | --- |
| `number` | string | Required. Normalized and must be 7-15 digits. |
| `sender_id` | string | Required, max 11 characters. Used as the message sender. |
| `message` | string | Required, must contain `%otp_code%`. `%expiry%` is also substituted. |
| `expiry` | int | Required, 1-10 minutes. Drives the `expiresAt` timestamp. |
| `length` | int | Required, 6-15 characters. |
| `medium` | string | Required, `sms` or `voice`. `voice` is recorded only; no audio plays. |
| `type` | string | Required, `numeric` or `alphanumeric`. |

### Success response (Arkesel)

```json
{ "code": "1000", "ussd_code": "*928*01#", "message": "Successful, OTP is being processed for delivery" }
```

`ussd_code` is Arkesel's real fallback string and is returned verbatim for shape fidelity.

### Validation response

All generate validation failures use HTTP `422` and code `1001` (the only code Arkesel
documents for this endpoint), with the specific reason as the message:

| Problem | Message |
| --- | --- |
| `type` not numeric/alphanumeric | `The type you selected [numeri] is invalid. Expected one of [numeric, alphanumeric]` |
| `medium` not sms/voice | `The medium you selected [fax] is invalid. Expected one of [sms, voice]` |
| `number` missing / invalid | `The number field is required.` / `The number field must be a valid phone number.` |
| `sender_id` missing / too long | `The sender_id field is required.` / `The sender_id field must not be greater than 11 characters.` |
| `message` missing / no placeholder | `The message field is required.` / `The message field must contain the %otp_code% placeholder.` |
| `expiry` missing / out of range | `The expiry field is required.` / `The expiry field must be between 1 and 10 minutes.` |
| `length` missing / out of range | `The length field is required.` / `The length field must be between 6 and 15 characters.` |
| `forceError` = `500` | HTTP `500`, code `1011`, `Internal error` |

## `POST /api/otp/verify`

### Request body

| Field | Type | Emulated behaviour |
| --- | --- | --- |
| `code` | string | Required. Compared case-insensitively against the issued code. |
| `number` | string | Required. Normalized, so `+233 54 491 9953` matches `233544919953`. |

### Responses

| Situation | Status | Code | Message | Source |
| --- | --- | --- | --- | --- |
| Verified | `200` | `1100` | `Successful` | Arkesel |
| `code` missing | `422` | `1101` | `The code field is required.` | Arkesel |
| `number` missing | `422` | `1101` | `The number field is required.` | Msgtrap |
| Wrong code, or no code issued to that number | `422` | `1103` | `Invalid code.` | Msgtrap |
| Correct code, past expiry | `422` | `1104` | `Code has expired. Request a new one.` | Msgtrap |
| Correct code, already verified | `422` | `1105` | `Code has already been used. Request a new one.` | Msgtrap |
| `forceError` = `500` | `500` | `1106` | `Internal error` | Arkesel |

Arkesel publishes `1100`, `1101` and `1106` but no codes for wrong, expired or reused codes.
Msgtrap continues the same numeric scheme (`1103`-`1105`) rather than inventing a new shape.
Codes remain `{"code":"...","message":"..."}` — no `status` field, matching the OTP API.

### Semantics

- **Latest wins.** Issuing a new code for a number replaces the previous one.
- **Single use.** A verified code is never accepted twice.
- **Wrong guesses** increment an attempt counter (shown in the UI) and never invalidate the
  real code, so you can test "3 failed attempts" UI without restarting.
- **Clearing the inbox** drops all outstanding OTPs.
- Verification updates the captured message, adding a `verified` entry to its timeline and a
  `verified` badge in the UI.

## Deliberate differences

- Nothing is ever forwarded to a carrier. There is no billing, coverage or routing logic.
- Message ids and OTP codes are generated locally, so they differ between runs.
- `scheduled_date` is not honoured as a timer; the message is captured immediately and left
  in the `scheduled` state.
- `medium: "voice"` never dials out or synthesises audio; the code is still captured so you
  can verify it.
- Number validation is intentionally permissive (7–15 digits) because test numbers are
  often fake. Arkesel's real coverage rules are not replicated.
