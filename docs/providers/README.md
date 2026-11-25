# Provider API coverage

Each adapter emulates a **subset** of its provider's API — enough to run real send flows in
development, not the whole platform. This page is the index; see the per-provider pages for
exact request/response shapes and what is deliberately missing.

| Provider | Coverage page | Endpoints implemented | Not implemented |
| --- | --- | --- | --- |
| Arkesel | [arkesel-coverage.md](../arkesel-coverage.md) | SMS send, balance, OTP generate/verify | History, reports, templates, contacts, voice, legacy v1 |
| Hubtel | [hubtel.md](hubtel.md) | `POST/GET /v1/messages/send` | Delivery reports, OTP service, payments, bulk campaigns |
| Twilio | [twilio.md](twilio.md) | `POST /2010-04-01/Accounts/{Sid}/Messages.json` | Media/MMS, list/fetch/update, Messaging Services, Verify |
| mNotify / BMS | [mnotify.md](mnotify.md) | `POST /api/sms/quick`, `GET /api/balance/sms` | Groups, contacts, templates, voice, USSD, IVR, reports |

The email channel is not a provider: it is plain SMTP on `:1025`, documented separately in
[../email.md](../email.md).

## Fidelity labels

Every behaviour in the per-provider pages carries one of these labels:

- **Published** — taken from the provider's own documentation or OpenAPI spec.
- **Inferred** — observed in real-world integrations because the provider does not publish it.
- **Msgtrap-defined** — invented by Msgtrap because the provider does not document the case.
  These follow the provider's envelope style so clients keep parsing, but they are not
  claimable as "exactly what the provider returns".

If you find a discrepancy, open an issue with the provider's doc link — fidelity bugs are
the ones worth fixing first.

## Known gaps across all providers

| Behaviour | Status |
| --- | --- |
| Delivery simulation | Shared: `queued` → `delivered` after `--deliver-after` |
| Segment counting | Shared: GSM-7 160/153, UCS-2 70/67 |
| Delivery callbacks | Per provider convention; implemented for all four send endpoints |
| Scheduling | Recorded, never delivered later |
| Failure injection | Arkesel only |
| Inbound SMS (MO) | Not implemented |
| Persistence | Not implemented by design |

## Email over SMTP

Email is a channel, not an adapter: one listener, one parser, one store, documented in
[../email.md](../email.md). It needs no provider id and no credentials, so it is unaffected
by `--provider`.
