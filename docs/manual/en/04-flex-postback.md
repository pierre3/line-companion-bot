[← Chapter 3](03-pet-growth-engine.md) | [Index](README.md) | [Chapter 5 →](05-rich-menu.md)

# Chapter 4 — Flex Message replies and postback dispatch

**What we're building:** `PetFlexMessageFactory` to render a status card, and swapping the webhook
handler's echo branch for real pet-care dispatch — a `PostbackEvent` carrying
`"action=feed"` / `"action=play"` / `"action=status"` now drives `PetGrowthEngine` and replies with
a Flex Message.

## Building the Flex Message by hand

`FlexBubble` / `FlexBox` / `FlexText` are plain generated POCOs — `Line.OpenApi.Messaging` has no
facade for assembling them (unlike `RichMenuClient` for rich menus in the next chapter). So
`PetFlexMessageFactory` is the one place that shape gets built by hand. Create
`src/LineCompanionBot/Services/PetFlexMessageFactory.cs`:

```csharp
using Line.OpenApi.Messaging.Generated.Api.Models;

namespace LineCompanionBot.Services;

// What a feed/play action just restored, surfaced on the status card so the three actions don't all
// render identically. Null on a plain status check (nothing was restored).
public sealed record CareFeedback(int HungerGain, int HappinessGain);

public static class PetFlexMessageFactory
{
    public static FlexMessage BuildStatus(PetState state, int rareFoodCount, CareFeedback? feedback = null)
    {
        var level = PetGrowthEngine.Level(state);
        var stage = PetGrowthEngine.Stage(state);
        var xpToNext = PetGrowthEngine.XpPerLevel - state.Xp % PetGrowthEngine.XpPerLevel;

        var contents = new List<FlexComponent>();

        // Prominent centered banner for the amount the just-taken action restored, so feed/play/status
        // read differently. Given its own line at the top rather than appended to a bar, which overflowed
        // the card width on narrow screens.
        var banner = GainBanner(feedback);
        if (banner is not null)
            contents.Add(banner);

        contents.Add(new FlexText { Type = "text", Text = $"{StageEmoji(stage)} Lv.{level} ({stage})", Weight = FlexText_weight.Bold, Size = "lg", Margin = banner is null ? null : "md" });
        contents.Add(new FlexText { Type = "text", Text = $"XP {state.Xp} · {xpToNext} to next Lv", Size = "sm", Margin = "md" }); // ·
        contents.Add(new FlexText { Type = "text", Text = $"Hunger {Bar(state.Hunger)} {(int)state.Hunger}%", Size = "sm", Margin = "md" });
        contents.Add(new FlexText { Type = "text", Text = $"Happy  {Bar(state.Happiness)} {(int)state.Happiness}%", Size = "sm" });

        // Only surfaced once the user actually owns rare food — no line (and no "x0" clutter) otherwise.
        if (rareFoodCount > 0)
            contents.Add(new FlexText { Type = "text", Text = $"\U0001F356 Golden Kibble ×{rareFoodCount}", Size = "sm", Margin = "md" }); // 🍖 ×

        var body = new FlexBox
        {
            Type = "box",
            Layout = FlexBox_layout.Vertical,
            Contents = contents,
        };

        var header = new FlexBox
        {
            Type = "box",
            Layout = FlexBox_layout.Vertical,
            Contents = new List<FlexComponent> { new FlexText { Type = "text", Text = state.Name, Weight = FlexText_weight.Bold, Size = "xl" } },
        };

        var altText = $"{state.Name}: Lv.{level}, Hunger {(int)state.Hunger}%, Happy {(int)state.Happiness}%";
        if (rareFoodCount > 0)
            altText += $", Golden Kibble x{rareFoodCount}";

        return new FlexMessage
        {
            Type = "flex",
            AltText = altText,
            Contents = new FlexBubble { Type = "bubble", Header = header, Body = body },
        };
    }

    public static FlexMessage BuildPlayRefused(PetState state)
    {
        var body = new FlexBox
        {
            Type = "box",
            Layout = FlexBox_layout.Vertical,
            Contents = new List<FlexComponent>
            {
                new FlexText { Type = "text", Text = $"{state.Name} is too hungry to play.", Weight = FlexText_weight.Bold, Wrap = true },
                new FlexText { Type = "text", Text = "Feed first, then try again.", Size = "sm", Margin = "md", Wrap = true },
            },
        };

        return new FlexMessage
        {
            Type = "flex",
            AltText = $"{state.Name} is too hungry to play.",
            Contents = new FlexBubble { Type = "bubble", Body = body },
        };
    }

    // A big, centered "+N" line for the stat the action just restored, or null on a plain status check.
    private static FlexText? GainBanner(CareFeedback? feedback)
    {
        if (feedback is { HungerGain: > 0 } fed)
            return new FlexText { Type = "text", Text = $"\U0001F354 +{fed.HungerGain}", Weight = FlexText_weight.Bold, Size = "xxl", Align = FlexText_align.Center, Color = "#F0932B" }; // 🍔
        if (feedback is { HappinessGain: > 0 } played)
            return new FlexText { Type = "text", Text = $"❤ +{played.HappinessGain}", Weight = FlexText_weight.Bold, Size = "xxl", Align = FlexText_align.Center, Color = "#EB4D4B" }; // ❤
        return null;
    }

    private static string StageEmoji(PetStage stage) => stage switch
    {
        PetStage.Hatchling => "\U0001F95A", // egg
        PetStage.Juvenile => "\U0001F423",  // hatching chick
        PetStage.Adult => "\U0001F414",     // chicken
        _ => "?",
    };

    private static string Bar(double percent)
    {
        var filled = Math.Clamp((int)Math.Round(percent / 10), 0, 10);
        return new string('█', filled) + new string('░', 10 - filled);
    }
}
```

The card `BuildStatus` draws varies by action. It always shows **Lv./stage**, **current XP and how
much is left to the next level**, and the Hunger/Happy bars, and on top of that:

- **On feed / play**, the amount restored appears as a big centered banner (`🍔 +N` / `❤ +N`), passed
  via `CareFeedback`; a plain `status` check shows none. It's a separate centered line rather than
  appended to a bar, which overflowed the card width on narrow screens.
- **When the user owns rare food (Golden Kibble)**, its count is shown. The count comes in as
  `rareFoodCount`, but the shop doesn't arrive until [Chapter 6](06-shop.md), so this chapter always
  passes `0` (i.e. no line).

That's what makes the three actions distinguishable at a glance.

`BuildPlayRefused` is the failure-branch counterpart, shown when `PetGrowthEngine.Play` returns
`Success: false`. Two design choices behind the card:

- **Stats render as a text progress bar** (`"█████░░░░░ 50%"`), not pet artwork. Flex images need a
  publicly reachable HTTPS URL, which would mean hosting image assets somewhere LINE's servers can
  reach — a real problem not worth solving to draw two stat bars. Text renders instantly with no
  asset hosting.
- **One input surface.** The bubble has no footer buttons. All pet care happens through the rich
  menu ([Chapter 5](05-rich-menu.md)); duplicating that with Flex buttons would be two ways to do
  the same thing.

> **Gotcha — set every node's `type`.** Notice each `FlexMessage`/`FlexBubble`/`FlexBox`/`FlexText`
> sets `Type` (`"flex"`/`"bubble"`/`"box"`/`"text"`). These are raw generated POCOs with no default
> for the `type` discriminator, and it is serialized only when set — leave it off and LINE rejects
> the reply body with a `400`. It's easy to miss because building the object succeeds offline; the
> gap only surfaces when you actually send, in [Chapter 9](09-end-to-end.md).

## Replacing the echo with postback dispatch

Now rewrite the event loop in `Endpoints/WebhookEndpoints.cs`. Replace Chapter 2's entire
`foreach (var ev in ...) { ... }` block with the one below, and add `IPetStore petStore` to the
handler parameters — just before `CancellationToken ct`. It's unconditionally registered, so no
`[FromServices]` is needed (contrast the gated `parser`/`messaging`). The new code references
`PetGrowthEngine`/`PetFlexMessageFactory` and `IPetStore`, so add `using LineCompanionBot.Services;`
and `using LineCompanionBot.Persistence;` to the top of the file:

```csharp
foreach (var ev in callback.Events ?? new())
{
    if (ev is not PostbackEvent { ReplyToken: { Length: > 0 } replyToken } postback || messaging is null)
        continue;
    // This pet is per-user; group/room sources carry no UserId and are skipped.
    if (postback.Source is not UserSource { UserId: { Length: > 0 } userId })
        continue;

    var now = DateTimeOffset.UtcNow;
    var pet = await petStore.GetOrCreateAsync(userId, now, ct);

    FlexMessage reply;
    switch (postback.Postback?.Data)
    {
        case "action=feed":
            var decayedBeforeFeed = PetGrowthEngine.ApplyDecay(pet, now);
            pet = PetGrowthEngine.Feed(pet, now);   // Chapter 6 upgrades this branch to consume Golden Kibble
            await petStore.SaveAsync(pet, ct);
            // No shop yet, so rareFoodCount is 0; Chapter 6 wires it to real inventory.
            reply = PetFlexMessageFactory.BuildStatus(
                pet, rareFoodCount: 0,
                new CareFeedback((int)Math.Round(pet.Hunger - decayedBeforeFeed.Hunger), 0));
            break;
        case "action=play":
            var decayedBeforePlay = PetGrowthEngine.ApplyDecay(pet, now);
            var played = PetGrowthEngine.Play(pet, now);
            await petStore.SaveAsync(played.State, ct);
            reply = played.Success
                ? PetFlexMessageFactory.BuildStatus(
                    played.State, rareFoodCount: 0,
                    new CareFeedback(0, (int)Math.Round(played.State.Happiness - decayedBeforePlay.Happiness)))
                : PetFlexMessageFactory.BuildPlayRefused(played.State);
            break;
        case "action=status":
            pet = PetGrowthEngine.Status(pet, now);
            await petStore.SaveAsync(pet, ct);
            reply = PetFlexMessageFactory.BuildStatus(pet, rareFoodCount: 0);
            break;
        default:
            continue; // unrecognized postback data
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
        app.Logger.LogWarning(ex, "Failed to reply to a postback event.");
    }
}
```

The resolve-user-then-dispatch-then-reply shape is the whole handler now. As in Chapter 2, the reply
call is wrapped in a try/catch that logs any failure but still lets the endpoint return 200 — an
expired reply token shouldn't provoke a retry storm. `CancellationToken ct` flows from `HttpContext.RequestAborted` (bound automatically —
no attribute) into the store and reply calls.

> The `"action=..."` strings aren't arbitrary — they're exactly the postback data the rich menu in
> [Chapter 5](05-rich-menu.md) is built to send. Dispatch is wired here first so the menu has
> something to talk to.

## Try it — simulate a postback

The event loop skips every event when `messaging` is `null` (the `|| messaging is null` guard):
`MessagingClient` is only registered when `LINE_CHANNEL_ACCESS_TOKEN` is set, so with only the
Chapter 2 secret in place the handler would `continue` past the `switch` without dispatching
anything. Set a placeholder access token too, so the client is registered and the dispatch actually
runs (the reply to `api.line.me` still fails — the token is fake — which is exactly what we want to
watch happen):

```powershell
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "demo-token" --project src/LineCompanionBot
```

With `LINE_CHANNEL_SECRET` still in user-secrets (from Chapter 2), self-sign a payload carrying a
`postback` event instead of a `message`:

```powershell
$body = '{"destination":"xxx","events":[{"type":"postback","replyToken":"dummy","source":{"type":"user","userId":"U123"},"postback":{"data":"action=feed"},"timestamp":1,"mode":"active"}]}'
# ...sign and POST exactly as in Chapter 2...
```

Set a breakpoint on the `switch` and F5-debug the request: you'll see it resolve `U123`, run
`Feed`, and build a `FlexMessage`. Without a real channel access token the reply call to
`api.line.me` fails and is logged — but the endpoint still returns 200: the same handling as
Chapter 2 (log the failure, return 200 anyway), now wrapping a real call out to LINE's API. Wiring a real token so the card actually arrives is
[Chapter 9](09-end-to-end.md).
