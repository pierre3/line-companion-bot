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

## The complete file

Here is the whole of `src/LineCompanionBot/Services/PurchaseReconciliationService.cs`. The next two
sections walk through `ExecuteAsync` and `PollOnceAsync` in turn. `NotifyPurchaseAsync` — the
grant-time notification — is stubbed here so the service compiles and the reconciliation loop stands
on its own; Chapter 8 fills in its body (and the two `using`s it then needs):

```csharp
using Line.OpenApi.Messaging;
using Line.OpenApi.MiniApp;
using LineCompanionBot.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LineCompanionBot.Services;

// There is no push webhook for IAP events — GetWebhookEventsAsync must be polled. Idempotent by
// design: IInventoryStore.Grant/Revoke key off OrderId, so re-scanning an overlapping window after
// a restart can never double-grant or double-revoke.
public sealed class PurchaseReconciliationService : BackgroundService
{
    private readonly CompanionSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PurchaseReconciliationService> _logger;
    private long _watermarkEpochSeconds;

    public PurchaseReconciliationService(
        CompanionSettings settings,
        IServiceScopeFactory scopeFactory,
        ILogger<PurchaseReconciliationService> logger)
    {
        _settings = settings;
        // Stores are resolved per-poll from a fresh DI scope (see PollOnceAsync) rather than taken
        // as direct constructor dependencies: this BackgroundService is a Singleton for the
        // process lifetime, but the I*Store implementations only happen to be Singleton today
        // (in-memory). A future RDB-backed store would typically be Scoped (per-request/per-unit-
        // of-work DbContext), and a Singleton can't hold a Scoped dependency directly (the
        // "captive dependency" problem) — resolving via scope here means that swap needs no change
        // in this class.
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Only purchases made from this point on are polled for — a fresh demo process has no
        // reason to re-scan the full 7-day history on every restart.
        _watermarkEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

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
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // A poll failure must not kill the loop — just retry on the next tick.
                _logger.LogWarning(ex, "Purchase reconciliation poll failed; will retry next tick.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Trailing safety margin: querying right up to the current instant risks missing an event
    // that completed moments ago but isn't indexed yet on LINE's side. A few seconds of overlap
    // costs nothing (Grant/Revoke are idempotent by OrderId) but closes that gap.
    private const int TrailingBufferSeconds = 5;

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

        // Advance the watermark only after a fully successful walk of every page — advancing
        // per-event would risk silently skipping the rest of a page if this loop is interrupted.
        do
        {
            var page = await miniApp.GetWebhookEventsAsync(
                _settings.ChannelAccessToken!, start, now, pageSize: 50, cursor: cursor, status: "SUCCESS", ct);

            foreach (var entry in page?.Events ?? new())
            {
                var ev = entry.Event;
                if (ev?.OrderId is null)
                {
                    continue;
                }

                // Only act on orders this app itself reserved — other IAP activity on the same
                // channel (if any) is none of this app's business.
                var order = await orders.TryGetAsync(ev.OrderId, ct);
                if (order is null)
                {
                    continue;
                }

                // Grant/notify the user LINE itself attributes the purchase to, not the
                // client-supplied value recorded at reserve time (see Program.cs's
                // /api/shop/reserve) — ev.UserId comes from LINE's own IAP webhook payload, so
                // it's the authoritative identity even if a caller supplied a bogus userId when
                // reserving. Log a mismatch since it's a signal the reserve request was spoofed.
                var userId = ev.UserId ?? order.UserId;
                if (ev.UserId is not null && ev.UserId != order.UserId)
                {
                    _logger.LogWarning(
                        "Order {OrderId} was reserved with userId {ReservedUserId} but LINE attributes it to {ActualUserId}; using the latter.",
                        order.OrderId, order.UserId, ev.UserId);
                }

                switch (ev.Type)
                {
                    case "purchaseComplete":
                        if (await inventory.GrantAsync(userId, order.OrderId, order.ProductId, ct))
                        {
                            _logger.LogInformation(
                                "Granted {ProductId} to {UserId} (order {OrderId}).",
                                order.ProductId, userId, order.OrderId);
                            await NotifyPurchaseAsync(userId, order.ProductId, miniApp, messaging, notifierTokens, ct);
                        }
                        break;
                    case "refundComplete":
                        await inventory.RevokeAsync(userId, order.OrderId, ct);
                        break;
                }
            }

            cursor = page?.NextCursor;
        } while (!string.IsNullOrEmpty(cursor));

        _watermarkEpochSeconds = now;
    }

    // Fires right after a successful GrantAsync to tell the user in chat. Chapter 8 implements the
    // real thing (prefer a branded service message, fall back to a plain push); it's stubbed here so
    // Chapter 7 compiles and the reconciliation loop is complete on its own.
    private Task NotifyPurchaseAsync(
        string userId,
        string productId,
        MiniAppClient miniApp,
        MessagingClient messaging,
        INotifierTokenStore notifierTokens,
        CancellationToken ct)
        => Task.CompletedTask;
}
```

## The polling loop

`ExecuteAsync` ticks a `PeriodicTimer` every `PollSeconds`. A poll failure is caught and retried on
the next tick — the same idea as the webhook handler (log
the error, keep going), applied to a background loop. (This is also why `CompanionSettings.PollSeconds`
clamps to a positive value: `PeriodicTimer`'s constructor is *outside* this try/catch, and it throws
on a non-positive interval, which would take down the host.)

## One poll: walk every page, then advance the watermark

`PollOnceAsync` (in the file above) is the whole per-tick unit. Design decisions worth calling out:

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
