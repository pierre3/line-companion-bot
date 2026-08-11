using Line.OpenApi.Messaging;
using Line.OpenApi.Messaging.Generated.Api.Models;
using Line.OpenApi.MiniApp;
using Line.OpenApi.MiniApp.Models;
using LineCompanionBot.Persistence;
using LineCompanionBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LineCompanionBot.Endpoints;

// Grouped under /api/shop via MapGroup, following the minimal-API convention for organizing more
// than a couple of related routes (see also WebhookEndpoints.MapWebhookEndpoint).
public static class ShopEndpoints
{
    public static void MapShopEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/shop");

        // Development-only test hook is mapped only when the environment is Development, so a deployed
        // (Production) app never exposes it (see the /dev/complete-purchase endpoint below). Surfaced
        // to the front end via /config so shop.js can offer a "mark purchased" button.
        var isDev = app.Environment.IsDevelopment();

        group.MapGet("/config", (CompanionSettings settings) => Results.Ok(new { liffId = settings.LiffId, devPurchaseEnabled = isDev }));

        group.MapGet("/catalog", () => Results.Ok(ShopCatalog.Items));

        group.MapGet("/inventory/{userId}", async (string userId, IInventoryStore inventory, CancellationToken ct) =>
            Results.Ok(await inventory.GetAsync(userId, ct)));

        group.MapPost("/reserve", async (
            ShopReserveRequest req,
            CompanionSettings settings,
            MiniAppClient miniApp,
            IOrderStore orderStore,
            INotifierTokenStore notifierTokens,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!settings.HasMessaging)
                return Results.Problem("LINE_CHANNEL_ACCESS_TOKEN is not configured.", statusCode: 503);
            if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.ProductId) || string.IsNullOrWhiteSpace(req.LiffAccessToken))
                return Results.Problem("userId, productId, and liffAccessToken are required.", statusCode: 400);

            var item = ShopCatalog.Find(req.ProductId);
            if (item is null)
                return Results.Problem($"Unknown productId '{req.ProductId}'.", statusCode: 404);

            // Known simplification: req.UserId is trusted as supplied by the caller rather than derived
            // from LiffAccessToken (Line.OpenApi.MiniApp exposes no token-introspection call to verify
            // it server-side). This only affects local bookkeeping (IOrderStore) — PurchaseReconciliationService
            // grants/notifies using the userId LINE's own IAP webhook payload attributes the purchase to,
            // not this value, so a caller cannot redirect a real purchase's grant/notification elsewhere.

            // Best-effort: the notifier endpoints require a stateless/short-lived channel token, which
            // this app's single LINE_CHANNEL_ACCESS_TOKEN may not be. A failure here just means the later
            // purchase notification (Chapter 8) falls back to a plain push — it never blocks the purchase.
            try
            {
                var notifierToken = await miniApp.IssueNotificationTokenAsync(settings.ChannelAccessToken!, req.LiffAccessToken);
                if (notifierToken is not null)
                {
                    await notifierTokens.SaveAsync(req.UserId, notifierToken, ct);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Failed to issue a notifier token for {UserId}; purchase notification will fall back to push.", req.UserId);
            }

            // Known simplification: not validated against a trusted-proxy allowlist (no
            // UseForwardedHeaders middleware configured), so a direct caller can set this header to
            // anything — this value is meant as an anti-fraud signal to LINE's ReserveProductAsync, so
            // treat it as best-effort only, not a verified client IP.
            var clientIp = http.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            if (string.IsNullOrEmpty(clientIp))
            {
                clientIp = http.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
            }

            IapReserveResult? reserved;
            try
            {
                // ClientOs defaults to "android" only for direct/test callers that skip the front end;
                // shop.js always supplies the real value from liff.getOS().
                reserved = await miniApp.ReserveProductAsync(
                    req.LiffAccessToken, clientIp, req.ClientOs ?? "android", item.ProductId, item.Name);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Failed to reserve product {ProductId} for {UserId}.", item.ProductId, req.UserId);
                return Results.Problem("Failed to reserve the purchase with LINE.", statusCode: 502);
            }

            if (reserved?.OrderId is null)
                return Results.Problem("LINE did not return an order id.", statusCode: 502);

            // CancellationToken.None, not ct: LINE has already committed the order by this point (the
            // ReserveProductAsync call above succeeded), so a client disconnecting right now must not be
            // allowed to drop this local record — PurchaseReconciliationService can only match the
            // eventual purchaseComplete event back to a user/product if this write actually lands.
            await orderStore.RecordAsync(reserved.OrderId, req.UserId, item.ProductId, CancellationToken.None);
            return Results.Ok(new { orderId = reserved.OrderId });
        });

        if (isDev)
        {
            // Development-only: stand in for a completed IAP purchase. LINE MINI App in-app purchase
            // needs an approved IAP review (weeks-long, Japan-only, business) before even test
            // payments work, so the grant/notify path can't be driven from a real purchase locally.
            // This grants the item and sends the same push PurchaseReconciliationService would on a
            // purchaseComplete event, letting the downstream flow (inventory, chat notification,
            // Golden Kibble consumption on feed) be verified without a real payment. Mapped only in
            // the Development environment, so it never exists in a deployed app. It does NOT touch
            // LINE's IAP endpoints, so it works even when isApiAvailable('iap') is false.
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

                // Synthesize a unique order id (dev- prefixed so it never collides with a real LINE
                // order). GrantAsync keys off OrderId exactly as in the reconciliation path; since
                // each dev click is a distinct order, repeated clicks stack items — handy for testing
                // consumption — rather than being deduped.
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

                // notified reflects whether the push was actually attempted: without a configured
                // LINE_CHANNEL_ACCESS_TOKEN there is no MessagingClient, so the item is granted but no
                // chat message is sent — the front end uses this to avoid claiming otherwise.
                return Results.Ok(new { orderId, granted, notified = granted && messaging is not null });
            });
        }
    }
}

// The front end supplies userId/clientOs itself (from liff.getProfile()/liff.getOS()) since
// ReserveProductAsync's other inputs don't yield a LINE user id on their own.
public sealed record ShopReserveRequest(string UserId, string ProductId, string LiffAccessToken, string? ClientOs);

// Body for the Development-only /api/shop/dev/complete-purchase test hook.
public sealed record DevCompletePurchaseRequest(string UserId, string ProductId);
