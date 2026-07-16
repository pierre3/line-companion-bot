using Line.OpenApi.Messaging;
using Line.OpenApi.Messaging.Generated.Api.Models;
using Line.OpenApi.MiniApp;
using Line.OpenApi.MiniApp.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LineCompanionBot.Services;

// There is no push webhook for IAP events — GetWebhookEventsAsync must be polled. Idempotent by
// design: InventoryStore.Grant/Revoke key off OrderId, so re-scanning an overlapping window after
// a restart can never double-grant or double-revoke.
public sealed class PurchaseReconciliationService : BackgroundService
{
    private readonly CompanionSettings _settings;
    private readonly MiniAppClient _miniApp;
    private readonly OrderStore _orders;
    private readonly InventoryStore _inventory;
    private readonly NotifierTokenStore _notifierTokens;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PurchaseReconciliationService> _logger;
    private long _watermarkEpochSeconds;

    public PurchaseReconciliationService(
        CompanionSettings settings,
        MiniAppClient miniApp,
        OrderStore orders,
        InventoryStore inventory,
        NotifierTokenStore notifierTokens,
        IServiceProvider serviceProvider,
        ILogger<PurchaseReconciliationService> logger)
    {
        _settings = settings;
        _miniApp = miniApp;
        _orders = orders;
        _inventory = inventory;
        _notifierTokens = notifierTokens;
        // MessagingClient is only registered in DI when LINE_CHANNEL_ACCESS_TOKEN is set (see
        // Program.cs), which this service also requires to run at all — resolve it lazily via
        // IServiceProvider inside the already-gated ExecuteAsync path instead of taking a direct
        // constructor dependency, which the host would try (and fail) to resolve eagerly at
        // startup even when the token is unset.
        _serviceProvider = serviceProvider;
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

        var messaging = _serviceProvider.GetRequiredService<MessagingClient>();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.PollSeconds));
        do
        {
            try
            {
                await PollOnceAsync(messaging, stoppingToken);
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

    private async Task PollOnceAsync(MessagingClient messaging, CancellationToken ct)
    {
        var start = _watermarkEpochSeconds;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - TrailingBufferSeconds;
        string? cursor = null;

        // Advance the watermark only after a fully successful walk of every page — advancing
        // per-event would risk silently skipping the rest of a page if this loop is interrupted.
        do
        {
            var page = await _miniApp.GetWebhookEventsAsync(
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
                if (!_orders.TryGet(ev.OrderId, out var order))
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
                        if (_inventory.Grant(userId, order.OrderId, order.ProductId))
                        {
                            _logger.LogInformation(
                                "Granted {ProductId} to {UserId} (order {OrderId}).",
                                order.ProductId, userId, order.OrderId);
                            await NotifyPurchaseAsync(userId, order.ProductId, messaging, ct);
                        }
                        break;
                    case "refundComplete":
                        _inventory.Revoke(userId, order.OrderId);
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
    private async Task NotifyPurchaseAsync(string userId, string productId, MessagingClient messaging, CancellationToken ct)
    {
        var itemName = ShopCatalog.Find(productId)?.Name ?? productId;

        if (_settings.TemplateName is not null && _notifierTokens.TryGet(userId, out var token) && token.NotificationToken is not null)
        {
            // Only the send call itself gates the fallback — bookkeeping after a successful send
            // (saving the renewed token) must never cause a duplicate push if it were to fail.
            NotifierToken? renewed = null;
            try
            {
                renewed = await _miniApp.SendServiceMessageAsync(
                    _settings.ChannelAccessToken!,
                    token.NotificationToken,
                    _settings.TemplateName,
                    new Dictionary<string, string> { ["itemName"] = itemName },
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Service message failed for {UserId}; falling back to push.", userId);
            }

            if (renewed is not null)
            {
                _notifierTokens.Save(userId, renewed);
                return;
            }
        }

        try
        {
            await messaging.Api.V2.Bot.Message.Push.PostAsync(new PushMessageRequest
            {
                To = userId,
                Messages = new List<Message> { new TextMessage { Text = $"You received: {itemName}!" } },
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
