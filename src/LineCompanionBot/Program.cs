using Line.OpenApi.Messaging;
using Line.OpenApi.Messaging.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Line.OpenApi.Messaging.Generated.Api.Models;
using Line.OpenApi.Messaging.Webhook;
using Line.OpenApi.Messaging.Webhook.DependencyInjection;
using Line.OpenApi.Messaging.Webhook.Generated.Models;
using Line.OpenApi.MiniApp;
using Line.OpenApi.MiniApp.DependencyInjection;
using Line.OpenApi.MiniApp.Models;
using LineCompanionBot;
using LineCompanionBot.Persistence;
using LineCompanionBot.Persistence.InMemory;
using LineCompanionBot.Services;

// "dotnet run -- setup": one-shot rich menu bootstrap. Handled before WebApplication is built —
// this is a local admin action, never an HTTP endpoint reachable over a dev tunnel.
if (args.Length > 0 && args[0] == "setup")
{
    await RichMenuBootstrapper.RunAsync(CompanionSettings.FromEnvironment(), "assets/richmenu.png");
    return;
}

var builder = WebApplication.CreateBuilder(args);

var settings = CompanionSettings.FromEnvironment();
builder.Services.AddSingleton(settings);

// Each Add* call is gated so the app always starts, even with nothing configured yet — the health
// endpoint below reports what's missing rather than the app refusing to boot.
if (settings.HasWebhook)
{
    builder.Services.AddLineWebhook(o => o.ChannelSecret = settings.ChannelSecret!);
}

if (settings.HasMessaging)
{
    builder.Services.AddLineMessaging(o => o.ChannelAccessToken = settings.ChannelAccessToken!);
}

// MiniAppClient takes tokens per call rather than via DI options, so this has no required config.
builder.Services.AddLineMiniApp();

builder.Services.AddInMemoryPersistence();
builder.Services.AddHostedService<PurchaseReconciliationService>();

var app = builder.Build();

app.UseStaticFiles(); // serves wwwroot/shop/* (the MINI App front-end)

app.MapGet("/", () => Results.Ok(new
{
    service = "LineCompanionBot",
    webhook = settings.HasWebhook ? "enabled" : "disabled (set LINE_CHANNEL_SECRET)",
    messaging = settings.HasMessaging ? "enabled" : "disabled (set LINE_CHANNEL_ACCESS_TOKEN)",
    shop = settings.HasShop ? "enabled" : "disabled (set LINE_MINIAPP_LIFF_ID)",
}));

app.MapGet("/api/shop/config", () => Results.Ok(new { liffId = settings.LiffId }));

app.MapGet("/api/shop/catalog", () => Results.Ok(ShopCatalog.Items));

app.MapGet("/api/shop/inventory/{userId}", async (string userId, IInventoryStore inventory, CancellationToken ct) =>
    Results.Ok(await inventory.GetAsync(userId, ct)));

app.MapPost("/api/shop/reserve", async (
    ShopReserveRequest req,
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
    // it server-side). This only affects local bookkeeping (OrderStore) — PurchaseReconciliationService
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

app.MapPost("/webhook", async (
    HttpRequest request,
    // [FromServices] is required here (not just idiomatic sugar): both are conditionally
    // registered (see the HasWebhook/HasMessaging gates above), so ASP.NET Core's automatic
    // DI-vs-body inference — which only recognizes types it can see registered at startup — can't
    // tell these apart from a body/route parameter and fails to build the route at all.
    [FromServices] WebhookRequestParser? parser,
    [FromServices] MessagingClient? messaging,
    IPetStore petStore,
    IInventoryStore inventory,
    CancellationToken ct) =>
{
    if (parser is null)
        return Results.Problem("LINE_CHANNEL_SECRET is not configured.", statusCode: 503);

    // Read the raw body bytes: the signature is computed over these exact bytes, so read them
    // before any model binding.
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms, ct);
    var body = ms.ToArray();
    var signature = request.Headers["x-line-signature"];

    CallbackRequest callback;
    try
    {
        callback = await parser.ParseAsync(body, signature);
    }
    catch (WebhookSignatureException) { return Results.Unauthorized(); }
    catch (WebhookPayloadException) { return Results.BadRequest(); }

    // Pet care is driven entirely by rich menu postbacks (feed/play/status) — the one input
    // surface, kept consistent rather than duplicating it with Flex quick-reply buttons too.
    foreach (var ev in callback.Events ?? new())
    {
        if (ev is not PostbackEvent { ReplyToken: { Length: > 0 } replyToken } postback || messaging is null)
            continue;
        if (postback.Source is not UserSource { UserId: { Length: > 0 } userId })
            continue;

        var now = DateTimeOffset.UtcNow;
        var pet = await petStore.GetOrCreateAsync(userId, now, ct);

        FlexMessage reply;
        switch (postback.Postback?.Data)
        {
            case "action=feed":
                // A purchased "rare-food" item is consumed for a full instant refill instead of
                // the usual partial gain; cosmetic items (hat/badge) have no feed-time effect.
                // CancellationToken.None for both calls below, not ct: TryConsumeAsync already
                // mutates state (removes the item) the instant it returns true, so the matching
                // SaveAsync of the pet's feed effect must not be skippable by a cancellation
                // landing between the two calls — otherwise the item is spent with nothing granted.
                pet = await inventory.TryConsumeAsync(userId, "rare-food", CancellationToken.None)
                    ? PetGrowthEngine.FeedRare(pet, now)
                    : PetGrowthEngine.Feed(pet, now);
                await petStore.SaveAsync(pet, CancellationToken.None);
                reply = PetFlexMessageFactory.BuildStatus(pet);
                break;
            case "action=play":
                var played = PetGrowthEngine.Play(pet, now);
                await petStore.SaveAsync(played.State, ct);
                reply = played.Success
                    ? PetFlexMessageFactory.BuildStatus(played.State)
                    : PetFlexMessageFactory.BuildPlayRefused(played.State);
                break;
            case "action=status":
                pet = PetGrowthEngine.Status(pet, now);
                await petStore.SaveAsync(pet, ct);
                reply = PetFlexMessageFactory.BuildStatus(pet);
                break;
            default:
                continue;
        }

        try
        {
            await messaging.Api.V2.Bot.Message.Reply.PostAsync(new ReplyMessageRequest
            {
                ReplyToken = replyToken,
                Messages = new List<Message> { reply },
            }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // A reply can fail (e.g. an expired reply token). Log it but still return 200
            // below: LINE retries any non-2xx response, which would duplicate deliveries.
            app.Logger.LogWarning(ex, "Failed to reply to a postback event.");
        }
    }

    // Always 200 quickly: LINE retries non-2xx and times out slow responses.
    return Results.Ok();
});

app.Run();

// The front end supplies userId/clientOs itself (from liff.getProfile()/liff.getOS()) since
// ReserveProductAsync's other inputs don't yield a LINE user id on their own.
public sealed record ShopReserveRequest(string UserId, string ProductId, string LiffAccessToken, string? ClientOs);
