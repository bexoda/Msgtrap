# mNotify (BMS)

Emulated: **mNotify / BMS API v2** (`https://api.mnotify.com`).

Docs: <https://developer.bms.africa/> (OpenAPI: `developer.bms.africa/openapi15.yaml`)

## Endpoints

| Method | Path | Notes |
| --- | --- | --- |
| `POST` | `/api/sms/quick?key=...` | Quick bulk SMS, JSON body |
| `GET` | `/api/balance/sms?key=...` | Fake SMS credit balance |

## Authentication — Published

The API key travels as a **query parameter**, not a header:

```
?key=msgtrap-mnotify-key
```

| Credential | Default |
| --- | --- |
| `key` | `msgtrap-mnotify-key` |

Override with `--credential mnotify.key=…`.

## Request body — Published

```json
{
  "recipient": ["0241234567", "0201234567"],
  "sender": "mNotify",
  "message": "API messaging is fun!",
  "is_schedule": false,
  "schedule_date": "",
  "sms_type": "otp"
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `recipient` | Yes | Numbers in local or international form. |
| `sender` | Yes | Max 11 characters. |
| `message` | Yes | Message text. |
| `is_schedule` | No | When `true`, the message is captured in the `scheduled` state and never delivered. |
| `schedule_date` | No | `YYYY-MM-DD hh:mm`, recorded verbatim. |
| `sms_type` | No | e.g. `otp`. Recorded only; no special handling. |

## Success response — Published

```json
{
  "status": "success",
  "code": "2000",
  "message": "messages sent successfully",
  "summary": {
    "_id": "A59CCB70-662D-45EF-9976-1EFAD249793D",
    "type": "API QUICK SMS",
    "total_sent": 2,
    "international_dropped": 0,
    "contacts": 2,
    "total_rejected": 0,
    "numbers_sent": ["0241234567", "0201234567"],
    "credit_used": 2,
    "credit_left": 2001
  }
}
```

- `_id` is the campaign id: the message UUID, upper-cased like mNotify's.
- `credit_used` is `segments × recipients`, mirroring per-segment billing.
- `credit_left` is derived from the fake balance (`--sms-balance`).
- `international_dropped` is always `0`; Msgtrap does not model mNotify's coverage rules.

## Balance — Published

```json
{ "status": "success", "balance": 2003, "bonus": 0 }
```

`balance` comes from `--sms-balance`; `bonus` is always `0`.

## Errors — Msgtrap-defined

The mNotify spec documents only `200`, so these continue the same
`{status, code, message}` envelope with numeric-string codes:

| Situation | Status | `code` | `message` |
| --- | --- | --- | --- |
| Bad/missing key (enforcement on) | `401` | `1001` | `Invalid API key` |
| `message` missing | `422` | `1002` | `The message field is required.` |
| `sender` missing | `422` | `1003` | `The sender field is required.` |
| `recipient` missing/empty | `422` | `1004` | `The recipient field is required.` |
| `sender` longer than 11 | `422` | `1005` | `The sender field must not be greater than 11 characters.` |

## Not implemented

Groups, contacts, message templates, voice, USSD, IVR, campaigns, scheduled-campaign
management, delivery reports, sender-ID registration, and the international SMS endpoints.
