using Line.OpenApi.Messaging;
using Line.OpenApi.Messaging.Generated.Api.Models;
using Line.OpenApi.MiniApp;
using Line.OpenApi.MiniApp.Models;
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

    // Prefer a service message (richer, branded) but only when both prerequisites are actually
    // met for this specific user; otherwise — or on any failure — fall back to a plain push so
    // the user is always notified regardless of which piece is missing. Both the notifier
    // endpoints' stateless-token requirement and the reviewed-template requirement are easy to
    // not have in a fresh demo environment, so this fallback is the default path in practice, not
    // an edge case.
    private async Task NotifyPurchaseAsync(
        string userId,
        string productId,
        MiniAppClient miniApp,
        MessagingClient messaging,
        INotifierTokenStore notifierTokens,
        CancellationToken ct)
    {
        var itemName = ShopCatalog.Find(productId)?.Name ?? productId;

        var token = _settings.TemplateName is not null ? await notifierTokens.TryGetAsync(userId, ct) : null;
        if (token?.NotificationToken is not null)
        {
            // Only the send call itself gates the fallback — bookkeeping after a successful send
            // (saving the renewed token) must never cause a duplicate push if it were to fail.
            NotifierToken? renewed = null;
            try
            {
                renewed = await miniApp.SendServiceMessageAsync(
                    _settings.ChannelAccessToken!,
                    token.NotificationToken,
                    _settings.TemplateName!,
                    new Dictionary<string, string> { ["itemName"] = itemName },
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Service message failed for {UserId}; falling back to push.", userId);
            }

            if (renewed is not null)
            {
                await notifierTokens.SaveAsync(userId, renewed, ct);
                return;
            }
        }

        try
        {
            await messaging.Api.V2.Bot.Message.Push.PostAsync(new PushMessageRequest
            {
                To = userId,
                Messages = new List<Message> { new TextMessage { Type = "text", Text = $"You received: {itemName}!" } },
            }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Both notification paths failed: the item is durably granted (Chapter 7's
            // idempotency guarantee) but the user was never told. Nothing else retries this, so
            // surface it loudly rather than at Warning level.
            _logger.LogError(ex, "Both service message and push fallback failed for {UserId} — item was granted but never announced.", userId);
        }
    }
}
