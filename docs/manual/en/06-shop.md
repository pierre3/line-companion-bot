[← Chapter 5](05-rich-menu.md) | [Index](README.md) | [Chapter 7 →](07-reconciliation.md)

# Chapter 6 — MINI App shop: front end and backend

**What we're building:** the shop the rich menu's Shop button opens — a plain HTML/JS page served
from `wwwroot/shop`, backed by a small group of endpoints under `/api/shop`, plus the three
remaining persistence stores. This chapter also closes the Golden Kibble loop from Chapter 4.

## The catalog and the stores it needs

`src/LineCompanionBot/Services/ShopCatalog.cs` is a fixed three-item list — no admin CRUD, this is
a demo:

```csharp
namespace LineCompanionBot.Services;

public sealed record ShopItem(string ProductId, string Name, string Description);

public static class ShopCatalog
{
    public static readonly IReadOnlyList<ShopItem> Items = new List<ShopItem>
    {
        new("rare-food", "Golden Kibble", "A rare treat — refills Hunger to full instantly."),
        new("party-hat", "Party Hat", "A cosmetic hat for your companion."),
        new("star-badge", "Star Badge", "A shiny cosmetic badge to show off."),
    };
    public static ShopItem? Find(string productId) => Items.FirstOrDefault(i => i.ProductId == productId);
}
```

Three more `I*Store` interfaces join `IPetStore` under `Persistence/`, each with an `InMemory*`
implementation:

- **`IOrderStore`** — `RecordAsync(orderId, userId, productId)` at reserve time,
  `TryGetAsync(orderId)` at reconciliation time. Records which orders *this app* initiated.
- **`IInventoryStore`** — `GetAsync` (the inventory endpoint), `GrantAsync` / `RevokeAsync` (used by
  reconciliation, Chapter 7), and `TryConsumeAsync` (used by the feed branch below). `GrantAsync`
  keys off `OrderId` so a repeat no-ops — that idempotency is what makes reconciliation safe to
  re-run. Its in-memory implementation snapshots reads **under the same lock** the writes use, since
  `GetAsync` is reachable from an HTTP request concurrently with the background loop mutating the
  same `List<T>`.
- **`INotifierTokenStore`** — holds the latest `NotifierToken` per user (Chapter 8 sends with it).

Register all three in `AddInMemoryPersistence` alongside `IPetStore`:

```csharp
services.AddSingleton<IPetStore, InMemoryPetStore>();
services.AddSingleton<IOrderStore, InMemoryOrderStore>();
services.AddSingleton<IInventoryStore, InMemoryInventoryStore>();
services.AddSingleton<INotifierTokenStore, InMemoryNotifierTokenStore>();
```

## The backend endpoints

Create `src/LineCompanionBot/Endpoints/ShopEndpoints.cs`, grouped under `/api/shop` with
`MapGroup` (the minimal-API convention once you have more than a couple of related routes). Also
call `app.UseStaticFiles();` in `Program.cs` to serve `wwwroot/shop/*`, and `app.MapShopEndpoints();`
next to `app.MapWebhookEndpoint();`.

```csharp
using Line.OpenApi.MiniApp;
using Line.OpenApi.MiniApp.Models;
using LineCompanionBot.Persistence;
using LineCompanionBot.Services;
using Microsoft.Extensions.Logging;

namespace LineCompanionBot.Endpoints;

public static class ShopEndpoints
{
    public static void MapShopEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/shop");

        group.MapGet("/config", (CompanionSettings settings) => Results.Ok(new { liffId = settings.LiffId }));
        group.MapGet("/catalog", () => Results.Ok(ShopCatalog.Items));
        group.MapGet("/inventory/{userId}", async (string userId, IInventoryStore inventory, CancellationToken ct) =>
            Results.Ok(await inventory.GetAsync(userId, ct)));

        group.MapPost("/reserve", async (
            ShopReserveRequest req, CompanionSettings settings, MiniAppClient miniApp,
            IOrderStore orderStore, INotifierTokenStore notifierTokens, HttpContext http, CancellationToken ct) =>
        {
            if (!settings.HasMessaging)
                return Results.Problem("LINE_CHANNEL_ACCESS_TOKEN is not configured.", statusCode: 503);
            if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.ProductId) || string.IsNullOrWhiteSpace(req.LiffAccessToken))
                return Results.Problem("userId, productId, and liffAccessToken are required.", statusCode: 400);

            var item = ShopCatalog.Find(req.ProductId);
            if (item is null) return Results.Problem($"Unknown productId '{req.ProductId}'.", statusCode: 404);

            // Best-effort: notifier endpoints require a stateless/short-lived token this app's single
            // channel token may not be. A failure here only means Chapter 8 falls back to push — never fatal.
            try
            {
                var notifierToken = await miniApp.IssueNotificationTokenAsync(settings.ChannelAccessToken!, req.LiffAccessToken);
                if (notifierToken is not null) await notifierTokens.SaveAsync(req.UserId, notifierToken, ct);
            }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Failed to issue a notifier token for {UserId}; will fall back to push.", req.UserId); }

            var clientIp = http.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            if (string.IsNullOrEmpty(clientIp)) clientIp = http.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

            IapReserveResult? reserved;
            try
            {
                reserved = await miniApp.ReserveProductAsync(req.LiffAccessToken, clientIp, req.ClientOs ?? "android", item.ProductId, item.Name);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Failed to reserve product {ProductId} for {UserId}.", item.ProductId, req.UserId);
                return Results.Problem("Failed to reserve the purchase with LINE.", statusCode: 502);
            }
            if (reserved?.OrderId is null) return Results.Problem("LINE did not return an order id.", statusCode: 502);

            // CancellationToken.None, not ct: LINE has already committed the order, so a client disconnecting
            // now must not drop this record — reconciliation can only match the eventual purchaseComplete
            // back to a user/product if this write lands.
            await orderStore.RecordAsync(reserved.OrderId, req.UserId, item.ProductId, CancellationToken.None);
            return Results.Ok(new { orderId = reserved.OrderId });
        });
    }
}

public sealed record ShopReserveRequest(string UserId, string ProductId, string LiffAccessToken, string? ClientOs);
```

Points worth calling out:

- **Notifier token issuance happens here, not at purchase-completion time.**
  `IssueNotificationTokenAsync` needs the LIFF access token, which only the front end has, only while
  the user is actively in the shop. It's stored now so Chapter 7 can use it seconds-to-tens-of-seconds
  later when the purchase actually completes. It's wrapped in its own try/catch, separate from the
  reserve call — if issuing a notifier token fails (very possible), the purchase must still proceed.
- **Two known, documented simplifications (both flagged in code and the README).**
  `req.UserId` is trusted as supplied — `Line.OpenApi.MiniApp` exposes no server-side call to verify
  it from the LIFF token. This only affects local bookkeeping: Chapter 7 grants/notifies using the
  `userId` from LINE's *own* IAP webhook payload, so a caller can't redirect a real purchase's grant
  elsewhere. And `X-Forwarded-For` isn't validated against a trusted-proxy allowlist (no
  `UseForwardedHeaders`), so treat `clientIp` as a best-effort anti-fraud signal to LINE, not a
  verified value.

## The front end

`wwwroot/shop/index.html` loads the LIFF SDK from LINE's CDN and two local files:

```html
<script charset="utf-8" src="https://static.line-scdn.net/liff/edge/2/sdk.js"></script>
...
<p id="status">Loading…</p>
<ul id="catalog"></ul>
<script src="shop.js"></script>
```

`wwwroot/shop/shop.js` initializes LIFF from the server-provided `liffId`, renders the catalog, and
drives the purchase. The request body is where the front end supplies what the backend can't derive:

```js
await liff.init({ liffId: config.liffId });
if (!liff.isLoggedIn()) { liff.login(); return; }

const iapAvailable = liff.isApiAvailable('iap');   // if false, Buy buttons render disabled
// ...on Buy click:
const profile = await liff.getProfile();            // -> userId
const reserveResponse = await fetch('/api/shop/reserve', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    userId: profile.userId,
    productId: item.productId,
    liffAccessToken: liff.getAccessToken(),         // the token ReserveProductAsync needs
    clientOs: liff.getOS(),                         // "ios" | "android" (more reliable than UA)
  }),
});
const { orderId } = await reserveResponse.json();

await liff.iap.requestConsentAgreement();           // no-op if already agreed
await liff.iap.createPayment({ productId: item.productId, orderId }); // drives the store's purchase UI
```

Two client-side safeguards, both real fixes:

- **`isApiAvailable('iap')` is checked before rendering Buy, and again inside the click handler.**
  `reserve` commits a real order with LINE, so there's no point spending that commitment on a client
  that can never complete the purchase — hence the pre-check disables the button outright.
- **The Buy button is disabled for the whole reserve → consent → createPayment sequence.** Each step
  creates or spends a real LINE-side commitment; a second click mid-flight would reserve a *second*
  order for the same item with no way to cancel the first. It's re-enabled in a `finally`.

The IAP call sequence isn't wrapped by `Line.OpenApi.*` — it's client-side, MINI-App-specific
JavaScript in the LIFF SDK's `iap` namespace (confirmed against LINE's official MINI App IAP docs).
`createPayment` throws on cancellation/failure; the shop just surfaces the error and lets the user
retry, since Chapter 7 only ever grants inventory for orders that actually complete.

> **Known limitation:** if `createPayment` is cancelled or fails after `reserve` already succeeded,
> the `IOrderStore` entry and LINE's reserved order are never cleaned up — `Line.OpenApi.MiniApp`
> exposes no reservation-release call. Harmless (reconciliation only acts on orders that reach
> `purchaseComplete`), just a permanently unused record. Documented in the README rather than worked
> around.

## Closing the Golden Kibble loop

Now that Golden Kibble can be owned, upgrade Chapter 4's `feed` branch in `WebhookEndpoints.cs` to
consume it. Add `IInventoryStore inventory` to the handler parameters and change the branch:

```csharp
case "action=feed":
    // A purchased rare-food item is consumed for a full instant refill; cosmetics have no feed effect.
    // CancellationToken.None on both calls: TryConsumeAsync removes the item the instant it returns
    // true, so the matching SaveAsync must not be skippable by a cancellation landing between them —
    // otherwise the item is spent with nothing granted.
    pet = await inventory.TryConsumeAsync(userId, "rare-food", CancellationToken.None)
        ? PetGrowthEngine.FeedRare(pet, now)
        : PetGrowthEngine.Feed(pet, now);
    await petStore.SaveAsync(pet, CancellationToken.None);
    reply = PetFlexMessageFactory.BuildStatus(pet);
    break;
```

This is the payoff for `FeedRare` from Chapter 3 — a reviewer caught that the catalog *described*
Golden Kibble as refilling Hunger but nothing consumed it, so a reader would buy it and see no
effect. Now feeding while holding one spends it for a full refill.

## Try it — exercise the backend contract

Set a placeholder LIFF id so the shop registration activates, then F5:

```powershell
dotnet user-secrets set LINE_MINIAPP_LIFF_ID "1234567890-abcdefgh" --project src/LineCompanionBot
```

```powershell
Invoke-RestMethod http://localhost:5091/api/shop/catalog
# -> the 3-item catalog (Golden Kibble / Party Hat / Star Badge)

Invoke-RestMethod http://localhost:5091/api/shop/reserve -Method Post -ContentType 'application/json' -Body '{}'
# -> 400 (missing required fields)

Invoke-RestMethod http://localhost:5091/api/shop/reserve -Method Post -ContentType 'application/json' `
    -Body '{"userId":"U1","productId":"unknown-item","liffAccessToken":"fake"}'
# -> 404 (unknown productId)
```

The catalog/config/inventory endpoints respond, and the reserve endpoint's validation branches fire
*before* any network call. Opening the actual shop page needs a real MINI App channel and the LIFF
runtime — [Chapter 9](09-end-to-end.md).
