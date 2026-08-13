[← Chapter 8](08-notify.md) | [Index](README.md)

# Chapter 9 — End-to-end with a real channel, and troubleshooting

Every previous chapter verified its piece locally — signature round-trips, postback dispatch, Flex
construction, the shop's HTTP contract, the poll-and-retry loop — all without a
live LINE channel. This chapter is what's left: wiring a real channel so it all runs together, and
what tends to go wrong.

## Console setup, in the order that avoids dead ends

1. **Create a Messaging API channel** in the
   [LINE Developers Console](https://developers.line.biz/console/). Note its **channel secret** and
   issue a **channel access token** (the `line webhook` and `line richmenu` commands below use this
   token).
2. **Create a LINE MINI App channel** under the same provider. This is a distinct product from a
   regular LIFF app and has its own review / trial-user flow — add yourself as a **trial user** so
   you can test without full review. Note the **LIFF ID** it assigns, and issue a **channel access
   token** for this channel too — the LIFF app that backs the MINI App lives under *this* channel, so
   the `line liff` commands below need its token, not the Messaging channel's.
3. Getting this order wrong (trying to register a MINI App channel before understanding it needs its
   own provider setup, distinct from the Messaging API channel) is the most likely real-world
   stumbling block here — more so than anything in the code.

You do **not** set any endpoint URLs in the console anymore. Both LINE-side URLs — the Messaging
channel's **Webhook URL** and the MINI App's **endpoint URL** — are set from the `line` tool once you
have a dev tunnel, in "Bringing it up" below. The only thing that still requires a console click is
the one-time **Use webhook** toggle (noted there).

## Put the secrets in user-secrets

From Getting started, secrets live in `dotnet user-secrets`, never in a checked-in file:

```powershell
dotnet user-secrets set LINE_CHANNEL_SECRET       "<channel secret>"       --project src/LineCompanionBot
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "<channel access token>" --project src/LineCompanionBot
dotnet user-secrets set LINE_MINIAPP_LIFF_ID      "<liff id>"              --project src/LineCompanionBot
# optional, for the service-message path instead of push:
# dotnet user-secrets set LINE_MINIAPP_TEMPLATE_NAME "<approved template name>" --project src/LineCompanionBot
```

`BuildCompanionConfiguration` reads user-secrets only in the `Development` environment, which the F5
launch config sets (`ASPNETCORE_ENVIRONMENT=Development`), so the app picks these up. (The `line`
tool below reads channel access tokens from an env var / `--channel-token` / a `line config`
profile — not from user-secrets — and it needs *two* of them: the Messaging channel token for the
webhook/rich-menu commands and the MINI App channel token for the LIFF command. The app itself only
stores the one `LINE_CHANNEL_ACCESS_TOKEN`; the MINI App token is passed to `line liff` directly.)

## Bringing it up

The rich menu is a one-time registration; pointing LINE at your app is a handful of `line` commands
you re-run each time the tunnel URL changes — no console round-trip.

### One-time — register the rich menu

**Create and set the rich menu once** with the `line` tool ([Chapter 5](05-rich-menu.md)). Edit
`YOUR_LIFF_ID` in `richmenu.json` to your LIFF id first — that `https://liff.line.me/<LIFF_ID>` URL is
*permanent* (it redirects to whatever the LIFF app's endpoint currently is), so the menu never needs
touching when the tunnel changes. Give the tool the **Messaging** channel token (it doesn't read
user-secrets), then run the three steps — `create` prints the id you pass to the next two:

```powershell
$env:LINE_CHANNEL_ACCESS_TOKEN = "<messaging channel access token>"
line richmenu create --file src/LineCompanionBot/assets/richmenu.json   # prints the new id
line richmenu image  <richMenuId> --file src/LineCompanionBot/assets/richmenu.png
line richmenu set-default <richMenuId>
```

### Start the app and open a tunnel

1. **Start the app:** press **F5**.
2. **Expose it with a dev tunnel** so LINE can reach both your webhook and the shop page:

   ```powershell
   devtunnel user login       # first time only
   devtunnel host -p 5091 --allow-anonymous
   ```

   Note the forwarded HTTPS base URL it prints — called `https://<tunnel>` below.

### Point LINE at the tunnel — entirely from the CLI

The `line` tool sets both LINE-side URLs, so there's no console visit here. Two things to keep
straight: they live on **different channels** (so each command needs *that* channel's access token),
and the paths differ — the webhook is `/webhook`, the MINI App (LIFF) endpoint is `/shop/`.

```powershell
# 1. Webhook URL — Messaging channel token. (devtunnel blocks its terminal, and on a repeat
#    session you skip the one-time rich-menu step, so set it here rather than assume it carries over.)
$env:LINE_CHANNEL_ACCESS_TOKEN = "<messaging channel access token>"
line webhook set-endpoint --url https://<tunnel>/webhook
line webhook test-endpoint          # LINE probes the endpoint and reports reachability — replaces the console "Verify"

# 2. MINI App endpoint — the LIFF app is under the MINI App channel, so pass *its* token.
line liff list        --channel-token "<mini app channel access token>"   # find the liffId
line liff update-url <liffId> --url https://<tunnel>/shop/ --channel-token "<mini app channel access token>"
```

> **"Use webhook" is a one-time console toggle.** `set-endpoint` sets the URL but does not flip the
> channel's *Use webhook* switch — the Messaging API doesn't expose it. Run `line webhook get-endpoint`;
> if it reports `active: false`, turn **Use webhook** on once in the console. That's the only console
> click left, and you only do it once per channel — every later session is just the two commands above
> against the new tunnel URL.

Each time you restart the tunnel the forwarded URL changes, so re-run `webhook set-endpoint` and
`liff update-url` with the new host. Two commands, no console.

## Trying the full loop

1. Add the bot as a friend; the rich menu (Feed / Play / Status / Shop) should appear immediately —
   that's `line richmenu set-default` having taken effect.
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

## Development-only: simulate a purchase

Driving a *real* purchase end to end isn't possible locally. LINE MINI App in-app purchase requires
an approved **IAP review** (weeks-long, Japan-only, and for a business) before even test payments
work — and those only run in a Developing channel with registered testers — plus a separate
verification review before users can actually pay. Until all that is in place, **Buy** stays disabled
(`liff.isApiAvailable('iap')` is false) and the reconciliation poll logs a `403`. That's expected,
not a bug.

To still verify the *downstream* flow — grant → chat notification → Golden Kibble consumption on
Feed — the app exposes a **Development-only** stand-in for a completed purchase. It is mapped **only
when the environment is Development**: in Production the endpoint returns `404` and
`config.devPurchaseEnabled` is `false`, so none of this ships in a deployed app.

> **Caution — the dev hook is unauthenticated.** It grants items and pushes a message to any `userId`
> with no auth check. Harmless on `localhost`, but the launch step above hosts the Development server
> through `devtunnel … --allow-anonymous`: while that tunnel is open, anyone who has the URL can reach
> this endpoint too (e.g. to spam pushes to user ids they know). Keep the tunnel short-lived, don't
> share the URL, and close it when you're done testing. It never exists in Production.

Add the hook to `ShopEndpoints.cs` (it needs three more `using`s: `Line.OpenApi.Messaging`,
`Line.OpenApi.Messaging.Generated.Api.Models`, and `Microsoft.AspNetCore.Mvc`). Capture the
environment once, surface it through `/config`, and map the endpoint behind an `isDev` guard:

```csharp
var isDev = app.Environment.IsDevelopment();

group.MapGet("/config", (CompanionSettings settings) =>
    Results.Ok(new { liffId = settings.LiffId, devPurchaseEnabled = isDev }));

// ...the /reserve endpoint from Chapter 6...

if (isDev)
{
    // Stand in for a completed IAP purchase: grant the item and send the same push
    // PurchaseReconciliationService would on a purchaseComplete event, without touching LINE's IAP
    // endpoints — so it works even when isApiAvailable('iap') is false. Mapped only in Development.
    group.MapPost("/dev/complete-purchase", async (
        DevCompletePurchaseRequest req,
        [FromServices] MessagingClient? messaging,
        IOrderStore orderStore,
        IInventoryStore inventory,
        CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.ProductId))
            return Results.Problem("userId and productId are required.", statusCode: 400);

        var item = ShopCatalog.Find(req.ProductId);
        if (item is null)
            return Results.Problem($"Unknown productId '{req.ProductId}'.", statusCode: 404);

        var orderId = $"dev-{Guid.NewGuid():N}";
        await orderStore.RecordAsync(orderId, req.UserId, item.ProductId, ct);
        var granted = await inventory.GrantAsync(req.UserId, orderId, item.ProductId, ct);

        if (granted && messaging is not null)
        {
            try
            {
                await messaging.Api.V2.Bot.Message.Push.PostAsync(new PushMessageRequest
                {
                    To = req.UserId,
                    Messages = new List<Message> { new TextMessage { Type = "text", Text = $"You received: {item.Name}!" } },
                }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Dev complete-purchase: push failed for {UserId}.", req.UserId);
            }
        }

        return Results.Ok(new { orderId, granted, notified = granted && messaging is not null });
    });
}
```

with the request record alongside `ShopReserveRequest`:

```csharp
public sealed record DevCompletePurchaseRequest(string UserId, string ProductId);
```

`shop.js` renders a **Mark purchased (dev)** button next to each item when `config.devPurchaseEnabled`
is true, so it's clickable even though Buy is disabled:

```js
if (devPurchaseEnabled) {
  const devButton = document.createElement('button');
  devButton.textContent = 'Mark purchased (dev)';
  devButton.addEventListener('click', () => devComplete(item, devButton));
  li.appendChild(devButton);
}

async function devComplete(item, button) {
  button.disabled = true;
  try {
    const profile = await liff.getProfile();
    const res = await fetch('/api/shop/dev/complete-purchase', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userId: profile.userId, productId: item.productId }),
    });
    if (!res.ok) { statusEl.textContent = `Dev grant failed for ${item.name} (HTTP ${res.status}).`; return; }
    const result = await res.json();
    statusEl.textContent = result.notified
      ? `Dev: granted ${item.name}. Check the chat for the notification, then tap Feed.`
      : `Dev: granted ${item.name} (no push — LINE_CHANNEL_ACCESS_TOKEN is unset). Tap Feed.`;
  } catch (err) { statusEl.textContent = `Dev grant error: ${err.message ?? err}`;
  } finally { button.disabled = false; }
}
```

Tap **Mark purchased (dev)** on **Golden Kibble** (or `curl` the endpoint directly) and you should see
the "You received: Golden Kibble!" push in chat, the item in `GET /api/shop/inventory/{userId}`, and —
on the next **Feed** tap — Hunger refilled to full as the Kibble is consumed. That drives everything a
real `purchaseComplete` would, short of LINE's billing itself:

```powershell
curl -X POST http://localhost:5091/api/shop/dev/complete-purchase `
    -H 'Content-Type: application/json' -d '{"userId":"<your userId>","productId":"rare-food"}'
```

## Troubleshooting

- **Rich menu doesn't appear / tapping does nothing.** Confirm `line richmenu set-default` succeeded
  (`line richmenu get-default` should return the id). Check `GET /` reports `messaging: enabled`.
- **401 on `/webhook`.** `LINE_CHANNEL_SECRET` doesn't match the channel's.
- **Webhook events never arrive.** `line webhook get-endpoint` should show your *current* tunnel URL
  with `active: true`, and `line webhook test-endpoint` should report `success: true`. A stale URL
  from a previous tunnel session (you forgot to re-run `set-endpoint`) or `active: false` (the
  one-time **Use webhook** toggle is still off) are the usual causes.
- **Feed/Play/Status does nothing.** Check logs for "Failed to reply to a postback event" — usually
  an expired reply token (valid ~1 min) from testing too slowly, or a missing/invalid access token.
- **Shop button opens a blank page (or does nothing).** You created the rich menu with
  `YOUR_LIFF_ID` still unreplaced in `richmenu.json`, or `LINE_MINIAPP_LIFF_ID` is wrong, or the LIFF
  app's endpoint URL isn't pointed at this app's `/shop/` path. Check the current URL with
  `line liff list` and repoint it with `line liff update-url <liffId> --url https://<tunnel>/shop/`
  (both need the **MINI App** channel token) — a stale tunnel URL from a previous session is the
  usual cause. If you changed `YOUR_LIFF_ID` itself, re-run `line richmenu create`/`image`/`set-default`.
- **Purchase completes but no chat message.** Expected to take up to `LINE_MINIAPP_POLL_SECONDS` —
  there's no instant push. If it never arrives, check for `PurchaseReconciliationService` warnings
  (an invalid/expired token is the usual cause).
- **Service message never sends, always falls back to push.** Expected unless both
  `LINE_MINIAPP_TEMPLATE_NAME` is an *approved* template and the user opened the shop recently enough
  for a live notifier token — see Chapter 8. The push fallback is the intended default-safe path, not
  a bug.

## What's verified vs. what needs a live channel

Everything through Chapter 8 is confirmable locally: signature verification (accept *and* reject),
postback dispatch into `PetGrowthEngine` producing a real Flex Message, all shop endpoints
(config/catalog/inventory + reserve validation
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
