**English** | [日本語](README_ja.md)

# LineCompanionBot

A virtual companion-raising LINE bot combined with a LINE MINI App shop, built to showcase the
[`Line.OpenApi.*`](https://github.com/pierre3/line-openapi-dotnet) .NET client library family end
to end. Users care for a virtual pet via LINE chat (rich menu → postback → Flex reply) and can buy
rare food / cosmetic skins through the MINI App shop using In-App Purchase (IAP); completed
purchases are granted and the user is notified back in chat.

A companion hands-on tutorial walking through how this app was built — from `dotnet new` to the
full end-to-end loop, one chapter per implementation step — lives at
[`docs/manual/en/`](docs/manual/en/README.md).

## Requirements

- .NET 10 SDK
- A LINE Messaging API channel (for the bot) and a LINE MINI App channel (for the shop) —
  see the tutorial for console setup steps.

## Environment variables

| Variable | Purpose |
|---|---|
| `LINE_CHANNEL_SECRET` | Webhook signature verification |
| `LINE_CHANNEL_ACCESS_TOKEN` | Reply/Push/RichMenu/Notifier/IAP-polling channel token |
| `LINE_MINIAPP_LIFF_ID` | LIFF ID for the shop button's URIAction on the rich menu |
| `LINE_MINIAPP_TEMPLATE_NAME` | Approved service-message template name (optional; unset → push fallback) |
| `LINE_MINIAPP_POLL_SECONDS` | Purchase-reconciliation poll interval (default 30) |

## Running

Secrets are read from `dotnet user-secrets` in development (or from environment variables). The
tutorial assumes Visual Studio Code (F5 to run/debug via the committed `.vscode/` config), but the
CLI works too:

```powershell
dotnet user-secrets set LINE_CHANNEL_SECRET       "<channel secret>"       --project src/LineCompanionBot
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "<channel access token>" --project src/LineCompanionBot

# One-time: create and set the default rich menu
dotnet run --project src/LineCompanionBot -- setup

# Start the app
dotnet run --project src/LineCompanionBot
```

See [`docs/manual/en/`](docs/manual/en/README.md) for the full walkthrough, including VS Code
setup, dev tunnel setup for the webhook, and LINE Developers Console configuration for the MINI App
shop.

## Building / testing

```powershell
dotnet build
dotnet test
```

## Known limitations

- `POST /api/shop/reserve` trusts the client-supplied `userId` for its own bookkeeping
  (`Line.OpenApi.MiniApp` exposes no server-side call to verify it from the LIFF access token).
  This is mitigated at the point that matters: `PurchaseReconciliationService` grants and notifies
  using the `userId` LINE's own IAP webhook payload attributes the purchase to, not the
  client-supplied value, so a caller cannot redirect a real purchase's grant/notification to a
  different LINE user. `GET /api/shop/inventory/{userId}` has no identity check either — acceptable
  for a demo with no auth layer at all, since a LINE `userId` isn't meaningfully secret.
- The `X-Forwarded-For` header used to derive `clientIp` for `ReserveProductAsync` isn't validated
  against a trusted-proxy allowlist, so a direct caller can set it to anything. Treat it as a
  best-effort anti-fraud signal, not a verified client IP.
- If the client-side `liff.iap.createPayment()` call is cancelled or fails after `reserve` already
  succeeded, the `IOrderStore` entry and LINE's own reserved order are never cleaned up —
  `Line.OpenApi.MiniApp` exposes no reservation-release call. This is harmless (
  `PurchaseReconciliationService` only ever acts on an `OrderId` that actually reaches
  `purchaseComplete`), just a permanently unused record in the in-memory store.

## Status

Feature-complete per [`docs/manual/en/`](docs/manual/en/README.md) (all 9 chapters), plus a 3-role
review pass (code/security/test-arch, all CONCERNS-non-blocking) with fixes applied — the review
findings are folded into the chapters they touch rather than kept as a separate section. Verified
locally without a live LINE channel: signature verification, postback → Flex reply dispatch, the
`setup` CLI verb, all shop endpoints, and the purchase-reconciliation poll/retry loop reaching
`api.line.me`. Full end-to-end behavior (chat replies, rich menu rendering, a completed IAP
purchase) requires wiring a real Messaging API + MINI App channel — see Chapter 9 of the tutorial.
