# Hubtel

Emulated: **Hubtel SMS API** (`https://smsc.hubtel.com`).

Docs: <https://developers.hubtel.com/>

## Endpoints

| Method | Path | Notes |
| --- | --- | --- |
| `POST` | `/v1/messages/send` | Basic auth + JSON `{from, to, content}` |
| `GET` | `/v1/messages/send` | Legacy query-string form with client credentials |

Hubtel sends one message per request, so `to` is a single number. Msgtrap also accepts a
comma-separated list (useful when fanning out a test) and answers with the first message id.

## Authentication

Two styles are accepted, because real integrations use both:

**Basic auth** (preferred):

```
Authorization: Basic base64(client-id:client-secret)
```

**Query/form credentials** (legacy):

```
?ClientId=...&ClientSecret=...
```

Field names are matched case-insensitively, so `clientid`, `ClientId` and `client_id` all work.

| Credential | Default |
| --- | --- |
| `client-id` | `msgtrap-hubtel-client` |
| `client-secret` | `msgtrap-hubtel-secret` |

Override with `--credential hubtel.client-id=…` / `--credential hubtel.client-secret=…`.

## Request fields

| Field | Aliases accepted | Required |
| --- | --- | --- |
| `from` | `From`, `sender` | No (stored as the sender) |
| `to` | `To`, `recipient`, `number` | Yes |
| `content` | `Content`, `message`, `body` | Yes |
| `Callback` | `callback`, `callbackUrl` | No |

## Success response — Published

```json
{
  "MessageId": "d1ec507c-2e55-4fdc-809f-891fb957c001",
  "Status": "Success",
  "Rate": 1,
  "NetworkId": "62002"
}
```

- `MessageId` is a UUID generated per recipient.
- `Rate` is always `1` (credits per message) — a plausible stand-in, not billing.
- `NetworkId` is a fixed fake network id. Hubtel resolves the real network from the MSISDN.

Some Hubtel clients parse `messageId`/`status` in camelCase; the container repo for this
adapter checks both casings, so either is handled by real-world code.

## Errors — Msgtrap-defined

Hubtel does not publish its error bodies, so these follow the same `{Status, Message}`
envelope:

| Situation | Status | Body |
| --- | --- | --- |
| Bad/missing credentials (enforcement on) | `401` | `{"Status":"Unauthorized","Message":"Invalid ClientId or ClientSecret"}` |
| Missing `To` | `400` | `{"Status":"BadRequest","Message":"The To field is required."}` |
| Missing `Content` | `400` | `{"Status":"BadRequest","Message":"The Content field is required."}` |
| No valid numbers | `400` | `{"Status":"BadRequest","Message":"No valid recipient numbers were supplied."}` |

## Delivery callback — Inferred

After the simulated delivery Msgtrap performs, once per recipient:

```
GET <callback>?MessageId=<id>&Status=Success&To=<number>
```

## Not implemented

Delivery reports, the Hubtel OTP service (`senderId`/`phoneNumber` request flow), payments,
bulk campaigns, and sender-ID registration.
