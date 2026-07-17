using Line.OpenApi.MiniApp;
using Line.OpenApi.MiniApp.Models;
using LineCompanionBot.Persistence;
using LineCompanionBot.Services;
using Microsoft.Extensions.Logging;

namespace LineCompanionBot.Endpoints;

// Grouped under /api/shop via MapGroup, following the minimal-API convention for organizing more
// than a couple of related routes (see also WebhookEndpoints.MapWebhookEndpoint).
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
    }
}

// The front end supplies userId/clientOs itself (from liff.getProfile()/liff.getOS()) since
// ReserveProductAsync's other inputs don't yield a LINE user id on their own.
public sealed record ShopReserveRequest(string UserId, string ProductId, string LiffAccessToken, string? ClientOs);
