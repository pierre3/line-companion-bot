[← Chapter 6](06-shop.md) | [Index](README.md) | [Chapter 8 →](08-notify.md)

# Chapter 7 — Purchase reconciliation

**What we're building:** `PurchaseReconciliationService`, a `BackgroundService` that discovers
completed purchases and grants the corresponding item — closing the loop opened by Chapter 6's
`reserve` call.

**Why polling, and why it's the one genuinely awkward part of the system.** `MiniAppClient` has no
push webhook for IAP events (unlike the Messaging webhook of Chapter 2). `GetWebhookEventsAsync` is
a *pull* API over a 7-day window, cursor-paginated. So instead of reacting instantly, this service
ticks on a timer (`LINE_MINIAPP_POLL_SECONDS`, default 30) and asks "what happened since I last
checked?"

## Registering it, and the lifetime subtlety

Add to `Program.cs`:

```csharp
builder.Services.AddHostedService<PurchaseReconciliationService>();
```

A `BackgroundService` is a **Singleton** for the whole process lifetime. The `InMemory*` stores
only *happen* to be Singleton today — a real RDB-backed store would typically be **Scoped** (a
`DbContext` per unit of work), and a Singleton can't hold a Scoped dependency directly (the "captive
dependency" problem — it would pin the first-ever `DbContext` forever). So the service takes an
`IServiceScopeFactory`, not the stores directly, and resolves everything from a fresh scope inside
each poll. That way, swapping store lifetimes later needs no change here — the same reason the
persistence seam from Chapter 3 exists.

```csharp
public PurchaseReconciliationService(CompanionSettings settings, IServiceScopeFactory scopeFactory, ILogger<...> logger)
{
    _settings = settings;
    _scopeFactory = scopeFactory;
    _logger = logger;
    // Only purchases from now on are polled for — a fresh demo process shouldn't re-scan 7 days of history.
    _watermarkEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
```

## The polling loop

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    if (!_settings.HasMessaging)
    {
        _logger.LogInformation("LINE_CHANNEL_ACCESS_TOKEN is not set — purchase reconciliation is disabled.");
        return;
    }

    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.PollSeconds));
    do
    {
        try { await PollOnceAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogWarning(ex, "Purchase reconciliation poll failed; will retry next tick."); }
    } while (await timer.WaitForNextTickAsync(stoppingToken));
}
```

A poll failure is swallowed and retried next tick — the same absorb-and-continue idiom as the
webhook handler, applied to a background loop. (This is also why `CompanionSettings.PollSeconds`
clamps to a positive value: `PeriodicTimer`'s constructor is *outside* this try/catch, and it throws
on a non-positive interval, which would take down the host.)

## One poll: walk every page, then advance the watermark

```csharp
private async Task PollOnceAsync(CancellationToken ct)
{
    using var scope = _scopeFactory.CreateScope();
    var services = scope.ServiceProvider;
    var miniApp = services.GetRequiredService<MiniAppClient>();
    var messaging = services.GetRequiredService<MessagingClient>();
    var orders = services.GetRequiredService<IOrderStore>();
    var inventory = services.GetRequiredService<IInventoryStore>();
    var notifierTokens = services.GetRequiredService<INotifierTokenStore>();

    var start = _watermarkEpochSeconds;
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - TrailingBufferSeconds;
    string? cursor = null;

    do
    {
        var page = await miniApp.GetWebhookEventsAsync(
            _settings.ChannelAccessToken!, start, now, pageSize: 50, cursor: cursor, status: "SUCCESS", ct);

        foreach (var entry in page?.Events ?? new())
        {
            var ev = entry.Event;
            if (ev?.OrderId is null) continue;

            // Only act on orders this app itself reserved.
            var order = await orders.TryGetAsync(ev.OrderId, ct);
            if (order is null) continue;

            // Grant/notify the user LINE itself attributes the purchase to (ev.UserId from LINE's own
            // payload), not the client-supplied value from reserve time — authoritative even if a
            // caller lied. Log a mismatch as a spoof signal.
            var userId = ev.UserId ?? order.UserId;
            if (ev.UserId is not null && ev.UserId != order.UserId)
                _logger.LogWarning("Order {OrderId} reserved with {Reserved} but LINE attributes it to {Actual}; using the latter.",
                    order.OrderId, order.UserId, ev.UserId);

            switch (ev.Type)
            {
                case "purchaseComplete":
                    if (await inventory.GrantAsync(userId, order.OrderId, order.ProductId, ct))
                    {
                        _logger.LogInformation("Granted {ProductId} to {UserId} (order {OrderId}).", order.ProductId, userId, order.OrderId);
                        await NotifyPurchaseAsync(userId, order.ProductId, miniApp, messaging, notifierTokens, ct); // Chapter 8
                    }
                    break;
                case "refundComplete":
                    await inventory.RevokeAsync(userId, order.OrderId, ct);
                    break;
            }
        }
        cursor = page?.NextCursor;
    } while (!string.IsNullOrEmpty(cursor));

    _watermarkEpochSeconds = now; // only after every page in the window succeeded
}
```

Design decisions worth calling out:

- **Only orders `IOrderStore` recognizes are acted on.** `MiniAppWebhookEvent` already carries
  `UserId`/`ProductId`, so `OrderStore` isn't needed to *resolve* the user — but it gates the loop so
  this app only grants for purchases *it* initiated via `reserve`, not any other IAP activity on the
  channel.
- **Grant/notify by `ev.UserId`, not the reserve-time `order.UserId`.** This is both a correctness fix
  (the webhook payload is authoritative) and the concrete mitigation for Chapter 6's trusted-`userId`
  simplification: even a spoofed reserve can't redirect the real purchaser's grant.
- **Idempotent by construction.** `GrantAsync` keys off `OrderId` and no-ops on a repeat, so a
  restart mid-window that re-scans an overlapping range double-grants nothing — no separate
  "processed events" set needed.
- **A trailing buffer** (`TrailingBufferSeconds = 5`): querying right up to *now* risks missing an
  event that completed moments ago but isn't indexed yet on LINE's side. A few seconds of overlap
  costs nothing (Grant/Revoke are idempotent) and closes the gap.
- **The watermark advances only after every page succeeds.** Advancing per-event would risk a silent
  gap if the loop were interrupted partway through a page.
- **Refunds handled symmetrically** — `refundComplete` revokes via the same `OrderId`.

## Try it

With no channel access token, the service logs that it's disabled and does nothing — the app stays
healthy either way:

```
info: LineCompanionBot.Services.PurchaseReconciliationService[0]
      LINE_CHANNEL_ACCESS_TOKEN is not set — purchase reconciliation is disabled.
```

Set a placeholder token in user-secrets and F5, and the service reaches the same network boundary as
Chapters 2/4 — with no outbound access it fails a poll, logs, and retries next tick, exactly as
designed. Confirming it actually finds a real `purchaseComplete` needs a live channel and a completed
purchase ([Chapter 9](09-end-to-end.md)). The notification that fires on a grant is wired next.
