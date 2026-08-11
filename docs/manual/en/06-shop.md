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

The files are the same interface-plus-in-memory-implementation pairs as Chapter 3's `IPetStore` /
`InMemoryPetStore`. Create them in turn, starting with
`src/LineCompanionBot/Persistence/IOrderStore.cs`:

```csharp
namespace LineCompanionBot.Persistence;

public sealed record ShopOrder(string OrderId, string UserId, string ProductId);

// Recorded at reserve time (Chapter 6), consulted at reconciliation time (Chapter 7) to confirm an
// IAP webhook event corresponds to an order this app actually initiated.
public interface IOrderStore
{
    Task RecordAsync(string orderId, string userId, string productId, CancellationToken ct = default);

    Task<ShopOrder?> TryGetAsync(string orderId, CancellationToken ct = default);
}
```

Its in-memory implementation, `src/LineCompanionBot/Persistence/InMemory/InMemoryOrderStore.cs`:

```csharp
using System.Collections.Concurrent;

namespace LineCompanionBot.Persistence.InMemory;

public sealed class InMemoryOrderStore : IOrderStore
{
    private readonly ConcurrentDictionary<string, ShopOrder> _orders = new();

    public Task RecordAsync(string orderId, string userId, string productId, CancellationToken ct = default)
    {
        _orders[orderId] = new ShopOrder(orderId, userId, productId);
        return Task.CompletedTask;
    }

    public Task<ShopOrder?> TryGetAsync(string orderId, CancellationToken ct = default)
        => Task.FromResult(_orders.TryGetValue(orderId, out var order) ? order : null);
}
```

Next, `src/LineCompanionBot/Persistence/IInventoryStore.cs`:

```csharp
namespace LineCompanionBot.Persistence;

public sealed record InventoryItem(string OrderId, string ProductId);

public interface IInventoryStore
{
    Task<IReadOnlyList<InventoryItem>> GetAsync(string userId, CancellationToken ct = default);

    // Keyed by OrderId so re-scanning an overlapping poll window (e.g. after a restart) can never
    // double-grant the same purchase — this is what makes reconciliation safe to run idempotently.
    Task<bool> GrantAsync(string userId, string orderId, string productId, CancellationToken ct = default);

    Task<bool> RevokeAsync(string userId, string orderId, CancellationToken ct = default);

    // Consumes one matching item (e.g. a single-use rare food), removing it. Safe to remove rather
    // than flag-as-used: the watermark never re-scans history after a restart, so there is no path
    // that could re-grant (and thus need to re-find) an already-consumed item.
    Task<bool> TryConsumeAsync(string userId, string productId, CancellationToken ct = default);
}
```

Its implementation, `src/LineCompanionBot/Persistence/InMemory/InMemoryInventoryStore.cs` —
grant/revoke/consume, plus the snapshot read taken **under the same lock** as the writes:

```csharp
using System.Collections.Concurrent;

namespace LineCompanionBot.Persistence.InMemory;

public sealed class InMemoryInventoryStore : IInventoryStore
{
    private readonly ConcurrentDictionary<string, List<InventoryItem>> _inventory = new();

    // Snapshotted under the same lock Grant/Revoke use — Get is reachable from a GET endpoint
    // concurrently with the background reconciliation loop mutating the same List<T>, which is
    // not thread-safe for an unsynchronized read against a locked writer.
    public Task<IReadOnlyList<InventoryItem>> GetAsync(string userId, CancellationToken ct = default)
    {
        if (!_inventory.TryGetValue(userId, out var list))
            return Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        lock (list) { return Task.FromResult<IReadOnlyList<InventoryItem>>(list.ToArray()); }
    }

    public Task<bool> GrantAsync(string userId, string orderId, string productId, CancellationToken ct = default)
    {
        var list = _inventory.GetOrAdd(userId, _ => new List<InventoryItem>());
        lock (list)
        {
            if (list.Any(i => i.OrderId == orderId)) return Task.FromResult(false);
            list.Add(new InventoryItem(orderId, productId));
            return Task.FromResult(true);
        }
    }

    public Task<bool> RevokeAsync(string userId, string orderId, CancellationToken ct = default)
    {
        if (!_inventory.TryGetValue(userId, out var list)) return Task.FromResult(false);
        lock (list) { return Task.FromResult(list.RemoveAll(i => i.OrderId == orderId) > 0); }
    }

    public Task<bool> TryConsumeAsync(string userId, string productId, CancellationToken ct = default)
    {
        if (!_inventory.TryGetValue(userId, out var list)) return Task.FromResult(false);
        lock (list)
        {
            var index = list.FindIndex(i => i.ProductId == productId);
            if (index < 0) return Task.FromResult(false);
            list.RemoveAt(index);
            return Task.FromResult(true);
        }
    }
}
```

Finally, `src/LineCompanionBot/Persistence/INotifierTokenStore.cs`:

```csharp
using Line.OpenApi.MiniApp.Models;

namespace LineCompanionBot.Persistence;

// Holds the latest NotifierToken per user. Overwritten whenever a token is (re-)issued or renewed
// by a send — no history needed, only the most recent token is ever usable.
public interface INotifierTokenStore
{
    Task SaveAsync(string userId, NotifierToken token, CancellationToken ct = default);

    Task<NotifierToken?> TryGetAsync(string userId, CancellationToken ct = default);
}
```

Its implementation, `src/LineCompanionBot/Persistence/InMemory/InMemoryNotifierTokenStore.cs`:

```csharp
using System.Collections.Concurrent;
using Line.OpenApi.MiniApp.Models;

namespace LineCompanionBot.Persistence.InMemory;

public sealed class InMemoryNotifierTokenStore : INotifierTokenStore
{
    private readonly ConcurrentDictionary<string, NotifierToken> _tokens = new();

    public Task SaveAsync(string userId, NotifierToken token, CancellationToken ct = default)
    {
        _tokens[userId] = token;
        return Task.CompletedTask;
    }

    public Task<NotifierToken?> TryGetAsync(string userId, CancellationToken ct = default)
        => Task.FromResult(_tokens.TryGetValue(userId, out var token) ? token : null);
}
```

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
call `app.UseDefaultFiles();` then `app.UseStaticFiles();` in `Program.cs` to serve `wwwroot/shop/*`
(the former rewrites `/shop/` to `/shop/index.html`, so Chapter 9's MINI App endpoint URL can point at
`/shop/`), and `app.MapShopEndpoints();` next to `app.MapWebhookEndpoint();`.

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

The three static files here (`index.html`, `shop.js`, `shop.css`) are shown only in excerpt below.
Copy the complete files verbatim from the reference repo at
[`src/LineCompanionBot/wwwroot/shop/`](https://github.com/pierre3/line-companion-bot/tree/main/src/LineCompanionBot/wwwroot/shop) —
unlike the backend, these are static assets that don't affect `dotnet build`, and you don't open the
page for real until [Chapter 9](09-end-to-end.md):

```powershell
New-Item -ItemType Directory -Force src/LineCompanionBot/wwwroot/shop | Out-Null
Copy-Item "path/to/line-companion-bot/src/LineCompanionBot/wwwroot/shop/*" src/LineCompanionBot/wwwroot/shop/
```

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
consume it. Add `IInventoryStore inventory` to the handler parameters — right after `IPetStore
petStore`, before `CancellationToken ct`; it lives in the `LineCompanionBot.Persistence` namespace
Chapter 4 already imported, so no new `using` — and change the branch:

```csharp
case "action=feed":
    // A purchased rare-food item is consumed for a full instant refill; cosmetics have no feed effect.
    // CancellationToken.None on both calls: TryConsumeAsync removes the item the instant it returns
    // true, so the matching SaveAsync must not be skippable by a cancellation landing between them —
    // otherwise the item is spent with nothing granted.
    var decayedBeforeFeed = PetGrowthEngine.ApplyDecay(pet, now);
    pet = await inventory.TryConsumeAsync(userId, "rare-food", CancellationToken.None)
        ? PetGrowthEngine.FeedRare(pet, now)
        : PetGrowthEngine.Feed(pet, now);
    await petStore.SaveAsync(pet, CancellationToken.None);
    // Show the actual hunger restored (post-clamp), not the nominal gain.
    reply = PetFlexMessageFactory.BuildStatus(
        pet,
        await RareFoodCountAsync(inventory, userId, ct),
        new CareFeedback((int)Math.Round(pet.Hunger - decayedBeforeFeed.Hunger), 0));
    break;
```

This is the payoff for `FeedRare` from Chapter 3 — a reviewer caught that the catalog *described*
Golden Kibble as refilling Hunger but nothing consumed it, so a reader would buy it and see no
effect. Now feeding while holding one spends it for a full refill.

The card also gains a **rare-food count**. Chapter 4 passed `rareFoodCount: 0` because there was no
inventory yet. Add a small helper to `WebhookEndpoints` (next to `MapWebhookEndpoint`), and switch the
`rareFoodCount: 0` in the `play`/`status` calls to `await RareFoodCountAsync(inventory, userId, ct)`:

```csharp
// Count of the consumable "rare-food" (Golden Kibble) the user currently owns, shown on the card.
private static async Task<int> RareFoodCountAsync(IInventoryStore inventory, string userId, CancellationToken ct)
{
    var items = await inventory.GetAsync(userId, ct);
    return items.Count(i => i.ProductId == "rare-food");
}
```

Now, whenever the user holds Golden Kibble, its count (`🍖 Golden Kibble ×N`) shows on every action's
card.

## Try it — exercise the backend contract

Set a placeholder LIFF id so the shop registration activates. You also need a placeholder
`LINE_CHANNEL_ACCESS_TOKEN` — `/reserve` short-circuits to **503** without one, *before* it reaches
the field/product validation below — but the `demo-token` from [Chapter 4](04-flex-postback.md)
already covers that if it's still in user-secrets. Then F5:

```powershell
dotnet user-secrets set LINE_MINIAPP_LIFF_ID "1234567890-abcdefgh" --project src/LineCompanionBot
# from Chapter 4; re-set it only if you've cleared user-secrets since:
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "demo-token" --project src/LineCompanionBot
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
