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
public static FlexMessage BuildStatus(PetState state)
{
    var level = PetGrowthEngine.Level(state);
    var stage = PetGrowthEngine.Stage(state);

    var body = new FlexBox
    {
        Layout = FlexBox_layout.Vertical,
        Contents = new List<FlexComponent>
        {
            new FlexText { Text = $"{StageEmoji(stage)} Lv.{level} ({stage})", Weight = FlexText_weight.Bold, Size = "lg" },
            new FlexText { Text = $"Hunger {Bar(state.Hunger)} {(int)state.Hunger}%", Size = "sm", Margin = "md" },
            new FlexText { Text = $"Happy  {Bar(state.Happiness)} {(int)state.Happiness}%", Size = "sm" },
        },
    };

    return new FlexMessage
    {
        AltText = $"{state.Name}: Lv.{level}, Hunger {(int)state.Hunger}%, Happy {(int)state.Happiness}%",
        Contents = new FlexBubble { Header = /* name header */, Body = body },
    };
}
```

`BuildPlayRefused` is the failure-branch counterpart, shown when `PetGrowthEngine.Play` returns
`Success: false`. Two design choices behind the card:

- **Stats render as a text progress bar** (`"█████░░░░░ 50%"`), not pet artwork. Flex images need a
  publicly reachable HTTPS URL, which would mean hosting image assets somewhere LINE's servers can
  reach — a real problem not worth solving to draw two stat bars. Text renders instantly with no
  asset hosting.
- **One input surface.** The bubble has no footer buttons. All pet care happens through the rich
  menu ([Chapter 5](05-rich-menu.md)); duplicating that with Flex buttons would be two ways to do
  the same thing.

## Replacing the echo with postback dispatch

Now rewrite the event loop in `Endpoints/WebhookEndpoints.cs`. Add `IPetStore petStore` to the
handler parameters (unconditionally registered, so no `[FromServices]` needed — contrast the gated
`parser`/`messaging`), and replace the `MessageEvent` echo branch:

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
            pet = PetGrowthEngine.Feed(pet, now);   // Chapter 6 upgrades this branch to consume Golden Kibble
            await petStore.SaveAsync(pet, ct);
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

The resolve-user-then-dispatch-then-reply shape is the whole handler now. The same absorb-and-ack
idiom from Chapter 2 still wraps the reply call — an expired reply token shouldn't provoke a
retry storm. `CancellationToken ct` flows from `HttpContext.RequestAborted` (bound automatically —
no attribute) into the store and reply calls.

> The `"action=..."` strings aren't arbitrary — they're exactly the postback data the rich menu in
> [Chapter 5](05-rich-menu.md) is built to send. Dispatch is wired here first so the menu has
> something to talk to.

## Try it — simulate a postback

With `LINE_CHANNEL_SECRET` still in user-secrets (from Chapter 2), self-sign a payload carrying a
`postback` event instead of a `message`:

```powershell
$body = '{"destination":"xxx","events":[{"type":"postback","replyToken":"dummy","source":{"type":"user","userId":"U123"},"postback":{"data":"action=feed"},"timestamp":1,"mode":"active"}]}'
# ...sign and POST exactly as in Chapter 2...
```

Set a breakpoint on the `switch` and F5-debug the request: you'll see it resolve `U123`, run
`Feed`, and build a `FlexMessage`. Without a real channel access token the reply call to
`api.line.me` fails and is logged — but the endpoint still returns 200, the same absorb-and-ack
idiom now covering a real downstream call. Wiring a real token so the card actually arrives is
[Chapter 9](09-end-to-end.md).
