[← Chapter 8](08-notify.md) | [Index](README.md)

# Chapter 9 — End-to-end with a real channel, and troubleshooting

Every previous chapter verified its piece locally — signature round-trips, postback dispatch, Flex
construction, the `setup` verb, the shop's HTTP contract, the poll-and-retry loop — all without a
live LINE channel. This chapter is what's left: wiring a real channel so it all runs together, and
what tends to go wrong.

## Console setup, in the order that avoids dead ends

1. **Create a Messaging API channel** in the
   [LINE Developers Console](https://developers.line.biz/console/). Note its **channel secret** and
   issue a **channel access token**.
2. **Create a LINE MINI App channel** under the same provider. This is a distinct product from a
   regular LIFF app and has its own review / trial-user flow — add yourself as a **trial user** so
   you can test without full review. Note the **LIFF ID** it assigns.
3. Getting this order wrong (trying to register a MINI App channel before understanding it needs its
   own provider setup, distinct from the Messaging API channel) is the most likely real-world
   stumbling block here — more so than anything in the code.

## Put the secrets in user-secrets

From Getting started, secrets live in `dotnet user-secrets`, never in a checked-in file:

```powershell
dotnet user-secrets set LINE_CHANNEL_SECRET       "<channel secret>"       --project src/LineCompanionBot
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "<channel access token>" --project src/LineCompanionBot
dotnet user-secrets set LINE_MINIAPP_LIFF_ID      "<liff id>"              --project src/LineCompanionBot
# optional, for the service-message path instead of push:
# dotnet user-secrets set LINE_MINIAPP_TEMPLATE_NAME "<approved template name>" --project src/LineCompanionBot
```

`BuildCompanionConfiguration` reads user-secrets only in the `Development` environment — both the F5
launch config and the `setup-richmenu` task set `ASPNETCORE_ENVIRONMENT=Development`, so both paths
pick these up.

## Bringing it up

1. **Create the rich menu once:** run the **setup-richmenu** task (*Terminal → Run Task →
   setup-richmenu*). It should print a rich menu id.
2. **Start the app:** press **F5**.
3. **Expose it with a dev tunnel** so LINE can reach your webhook:

   ```powershell
   devtunnel user login       # first time only
   devtunnel host -p 5091 --allow-anonymous
   ```

4. Set the forwarded HTTPS URL + `/webhook` as the channel's **Webhook URL** in the console, turn
   **Use webhook** on, and click **Verify**.

## Trying the full loop

1. Add the bot as a friend; the rich menu (Feed / Play / Status / Shop) should appear immediately —
   that's the setup task having taken effect.
2. Tap **Feed / Play / Status** — each produces a Flex status card within about a second. Tap
   **Play** while Hunger is low (decay is real-time) to see the refusal card.
3. Tap **Shop** to open the MINI App; it loads the catalog and lets you buy an item
   (`liff.iap.createPayment` drives the actual App Store / Play Store purchase UI — Chapter 6).
4. Once a purchase completes, `PurchaseReconciliationService` picks it up on its next poll tick
   (`LINE_MINIAPP_POLL_SECONDS`, default 30 — **not instant**, there's no push webhook) and a chat
   message announcing the new item arrives. Buy a **Golden Kibble**, then tap **Feed** — it refills
   Hunger to full and is consumed.

You can keep the VS Code debugger attached the whole time — set breakpoints in `WebhookEndpoints` or
`PurchaseReconciliationService` and watch real LINE traffic flow through.

## Troubleshooting

- **Rich menu doesn't appear / tapping does nothing.** Confirm the setup task printed a rich menu id
  (not the "not set" message). Check `GET /` reports `messaging: enabled`.
- **401 on `/webhook`.** `LINE_CHANNEL_SECRET` doesn't match the channel's.
- **Feed/Play/Status does nothing.** Check logs for "Failed to reply to a postback event" — usually
  an expired reply token (valid ~1 min) from testing too slowly, or a missing/invalid access token.
- **Shop button opens a blank page.** `LINE_MINIAPP_LIFF_ID` is wrong, or the MINI App channel's
  endpoint URL isn't pointed at this app's `/shop/` path — a console configuration issue, not a code
  one.
- **Purchase completes but no chat message.** Expected to take up to `LINE_MINIAPP_POLL_SECONDS` —
  there's no instant push. If it never arrives, check for `PurchaseReconciliationService` warnings
  (an invalid/expired token is the usual cause).
- **Service message never sends, always falls back to push.** Expected unless both
  `LINE_MINIAPP_TEMPLATE_NAME` is an *approved* template and the user opened the shop recently enough
  for a live notifier token — see Chapter 8. The push fallback is the intended default-safe path, not
  a bug.

## What's verified vs. what needs a live channel

Everything through Chapter 8 is confirmable locally: signature verification (accept *and* reject),
postback dispatch into `PetGrowthEngine` producing a real Flex Message, the `setup` verb's dispatch
and clean no-token exit, all shop endpoints (config/catalog/inventory + reserve validation
branches), and the reconciliation loop actually reaching `api.line.me` and handling responses
without crashing. What remains — a chat reply arriving, a rich menu rendering, and a completed IAP
purchase driving the full grant→notify path — needs the live channel setup above, which is why this
chapter exists separately rather than folding "trust me, it works" into the earlier ones.

## A note on the review gate

Before this app was considered done it went through a 3-role review gate (code / security /
test-architecture), all returning **CONCERNS (non-blocking)**, with the actionable findings fixed.
Those fixes aren't a separate appendix — they're folded into the chapters they touch: reconciliation
trusting LINE's own `ev.UserId` (Chapter 7), the tightened notify fallback and its `Error`-level
double-failure log (Chapter 8), the inventory read lock and the Golden Kibble consume that makes the
item actually do something (Chapters 6 and 3–4), and the extra `PetGrowthEngine` test cases (Chapter
3). So the code you built here *is* the reviewed, final shape — there's nothing left to unlearn.
