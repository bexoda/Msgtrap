# Twilio

Emulated: **Twilio Programmable Messaging — Message resource**.

Docs: <https://www.twilio.com/docs/messaging/api/message-resource>

## Endpoint

| Method | Path |
| --- | --- |
| `POST` | `/2010-04-01/Accounts/{AccountSid}/Messages.json` |

This is the path the official SDKs use to create a message; only the host changes when
pointing at Msgtrap.

## Authentication — Published

HTTP Basic, with the account SID as the username and the auth token as the password:

```
Authorization: Basic base64(AC00000000000000000000000000000000:msgtrap-twilio-token)
```

When enforcement is on, both the Basic credentials **and** the `{AccountSid}` in the path
must match. A wrong SID in the path returns `404` with code `20404`, matching Twilio's
behaviour for an unknown account.

| Credential | Default |
| --- | --- |
| `account-sid` | `AC00000000000000000000000000000000` |
| `auth-token` | `msgtrap-twilio-token` |

## Request fields — Published

Form-encoded (the SDK default); query parameters and a JSON body are also accepted for
manual testing.

| Field | Required | Notes |
| --- | --- | --- |
| `To` | Yes | E.164. Echoed back with a leading `+`. |
| `From` | No | Alphanumeric sender id or number. |
| `Body` | Yes | Message text. |
| `StatusCallback` | No | Called on simulated delivery. |

## Success response — Published shape

`201 Created`:

```json
{
  "sid": "SM4f2c9a7b0e14d0ea1c53b7a0d9e6f1c",
  "account_sid": "AC00000000000000000000000000000000",
  "to": "+233544919953",
  "from": "Msgtrap",
  "body": "Your code is 123456",
  "status": "queued",
  "num_segments": "1",
  "direction": "outbound-api",
  "api_version": "2010-04-01",
  "date_created": "Wed, 25 Nov 2026 09:41:02 +0000",
  "date_updated": "Wed, 25 Nov 2026 09:41:02 +0000",
  "uri": "/2010-04-01/Accounts/AC000…/Messages/SM4f2c….json",
  "price": null,
  "error_code": null,
  "error_message": null,
  "messaging_service_sid": null
}
```

- `sid` is `SM` followed by 32 hex characters.
- `num_segments` is a **string**, as Twilio returns it.
- `price` stays `null` because nothing is billed.

## Errors — Published codes

Twilio's error envelope is `{code, message, more_info, status}`:

| Situation | Status | `code` | `message` |
| --- | --- | --- | --- |
| Bad/missing credentials | `401` | `20003` | `Authentication Error - invalid username` |
| Account SID in path not found | `404` | `20404` | `The requested resource was not found` |
| `To` missing | `400` | `21604` | `A 'To' phone number is required.` |
| `To` invalid | `400` | `21211` | `The 'To' number … is not a valid phone number.` |
| `Body` missing | `400` | `21602` | `Message body is required.` |

## Delivery callback — Published shape

After the simulated delivery, Msgtrap posts form-encoded fields to `StatusCallback`, once
per recipient:

| Field | Value |
| --- | --- |
| `MessageSid`, `SmsSid` | the `SM…` id |
| `MessageStatus`, `SmsStatus` | `delivered` |
| `To` | normalized number |
| `AccountSid` | the configured fake SID |
| `ApiVersion` | `2010-04-01` |

## Not implemented

Media (`MediaUrl`, MMS), `List`/`Fetch`/`Update`/`Delete` on messages, Messaging Services
(`MessagingServiceSid` is echoed as `null`), content templates, Verify, and inbound webhooks.
