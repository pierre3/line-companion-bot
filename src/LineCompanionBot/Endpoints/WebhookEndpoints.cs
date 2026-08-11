using Line.OpenApi.Messaging;
using Line.OpenApi.Messaging.Generated.Api.Models;
using Line.OpenApi.Messaging.Webhook;
using Line.OpenApi.Messaging.Webhook.Generated.Models;
using LineCompanionBot.Persistence;
using LineCompanionBot.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LineCompanionBot.Endpoints;

public static class WebhookEndpoints
{
    public static void MapWebhookEndpoint(this WebApplication app)
    {
        app.MapPost("/webhook", async (
            HttpRequest request,
            // [FromServices] is required here (not just idiomatic sugar): both are conditionally
            // registered (see the HasWebhook/HasMessaging gates in Program.cs), so ASP.NET Core's
            // automatic DI-vs-body inference — which only recognizes types it can see registered
            // at startup — can't tell these apart from a body/route parameter and fails to build
            // the route at all.
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
                    case "action=play":
                        var decayedBeforePlay = PetGrowthEngine.ApplyDecay(pet, now);
                        var played = PetGrowthEngine.Play(pet, now);
                        await petStore.SaveAsync(played.State, ct);
                        reply = played.Success
                            ? PetFlexMessageFactory.BuildStatus(
                                played.State,
                                await RareFoodCountAsync(inventory, userId, ct),
                                new CareFeedback(0, (int)Math.Round(played.State.Happiness - decayedBeforePlay.Happiness)))
                            : PetFlexMessageFactory.BuildPlayRefused(played.State);
                        break;
                    case "action=status":
                        pet = PetGrowthEngine.Status(pet, now);
                        await petStore.SaveAsync(pet, ct);
                        reply = PetFlexMessageFactory.BuildStatus(pet, await RareFoodCountAsync(inventory, userId, ct));
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
    }

    // Count of the consumable "rare-food" (Golden Kibble) the user currently owns, shown on the card.
    private static async Task<int> RareFoodCountAsync(IInventoryStore inventory, string userId, CancellationToken ct)
    {
        var items = await inventory.GetAsync(userId, ct);
        return items.Count(i => i.ProductId == "rare-food");
    }
}
