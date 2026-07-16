# Tutorial: Building a Virtual Companion Bot + MINI App Shop

This is a hands-on walkthrough of how `LineCompanionBot` was built, step by step. It exists
alongside the app itself — each chapter here was written as its matching implementation step was
finished, so what you read is what actually happened, not a retrospective summary.

The goal isn't to re-explain what each `Line.OpenApi.*` package's API does — see the concept
articles in the [`line-dotnet` manual](https://github.com/pierre3/line-openapi-dotnet) for that.
The goal here is to show **how the packages wire together** into one realistic system: a LINE bot
where users raise a virtual pet via chat, plus a MINI App shop where they can buy items for it
with In-App Purchase (IAP).

## Chapter 1 — Project skeleton and DI wiring

**What we're building:** the smallest possible app that starts up, reports its own configuration
state, and does nothing else yet. Every later chapter builds on top of this.

**Why start here:** every `Line.OpenApi.*` sample app in `line-dotnet` follows the same shape —
plain environment-variable configuration (no `appsettings.json` binding), and the app always
*starts* even when nothing is configured, reporting what's missing via a health endpoint instead
of refusing to boot. Establishing that shape first means every later chapter can assume it.

**The code:**

`CompanionSettings.cs` reads everything the app needs from environment variables exactly once, at
startup:

```csharp
public sealed record CompanionSettings(
    string? ChannelSecret,
    string? ChannelAccessToken,
    string? LiffId,
    string? TemplateName,
    int PollSeconds)
{
    public bool HasWebhook => !string.IsNullOrWhiteSpace(ChannelSecret);
    public bool HasMessaging => !string.IsNullOrWhiteSpace(ChannelAccessToken);
    public bool HasShop => !string.IsNullOrWhiteSpace(LiffId);
    // FromEnvironment() reads LINE_CHANNEL_SECRET / LINE_CHANNEL_ACCESS_TOKEN /
    // LINE_MINIAPP_LIFF_ID / LINE_MINIAPP_TEMPLATE_NAME / LINE_MINIAPP_POLL_SECONDS.
}
```

`Program.cs` wires the three packages we'll use, each gated on whether its required config is
present:

```csharp
if (settings.HasWebhook)
    builder.Services.AddLineWebhook(o => o.ChannelSecret = settings.ChannelSecret!);

if (settings.HasMessaging)
    builder.Services.AddLineMessaging(o => o.ChannelAccessToken = settings.ChannelAccessToken!);

// MiniAppClient takes tokens per call rather than via DI options, so this needs no config at all.
builder.Services.AddLineMiniApp();
```

Note that `AddLineMiniApp()` takes no required configuration — unlike `AddLineWebhook`/
`AddLineMessaging`, `MiniAppClient`'s methods all take channel/user access tokens as plain
arguments per call (see the `MiniAppClient` XML docs), so there's nothing to gate it on.

**Try it:**

```powershell
cd src/LineCompanionBot
dotnet run
```

```powershell
Invoke-RestMethod http://localhost:5091/
```

```json
{
  "service": "LineCompanionBot",
  "webhook": "disabled (set LINE_CHANNEL_SECRET)",
  "messaging": "disabled (set LINE_CHANNEL_ACCESS_TOKEN)",
  "shop": "disabled (set LINE_MINIAPP_LIFF_ID)"
}
```

The app runs with zero configuration and tells you exactly what to set next — this is the pattern
every later chapter's feature will slot into.

## Chapter 2 — Webhook receive + echo reply

**What we're building:** `POST /webhook`, wired exactly like the `Line.OpenApi.Samples.Webhook`
sample in `line-dotnet`: verify the signature, deserialize the body, and for now just echo text
messages back. Later chapters replace the echo branch with pet-care postback dispatch — starting
from a known-working baseline first.

**The code** (`Program.cs`):

```csharp
app.MapPost("/webhook", async (
    HttpRequest request,
    [FromServices] WebhookRequestParser? parser,
    [FromServices] MessagingClient? messaging) =>
{
    if (parser is null)
        return Results.Problem("LINE_CHANNEL_SECRET is not configured.", statusCode: 503);

    // The signature is computed over the raw bytes, so read them before any model binding.
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms);
    var body = ms.ToArray();
    var signature = request.Headers["x-line-signature"];

    CallbackRequest callback;
    try { callback = await parser.ParseAsync(body, signature); }
    catch (WebhookSignatureException) { return Results.Unauthorized(); }
    catch (WebhookPayloadException) { return Results.BadRequest(); }

    foreach (var ev in callback.Events ?? new())
    {
        if (ev is MessageEvent { Message: TextMessageContent text } message && messaging is not null)
        {
            try
            {
                await messaging.Api.V2.Bot.Message.Reply.PostAsync(new ReplyMessageRequest
                {
                    ReplyToken = message.ReplyToken,
                    Messages = new List<Message> { new TextMessage { Text = $"echo: {text.Text}" } },
                });
            }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Failed to reply."); }
        }
    }

    // Always 200 quickly: LINE retries any non-2xx response, which would duplicate deliveries.
    return Results.Ok();
});
```

The "absorb downstream failures, always ack 200" idiom matters here specifically because LINE's
webhook delivery retries non-2xx responses — a reply failure (e.g. an expired reply token, valid
for about a minute) shouldn't turn into a duplicate-delivery storm.

**Try it** (no real LINE channel needed yet — self-sign a payload exactly like LINE would):

```powershell
$env:LINE_CHANNEL_SECRET = "demo-secret"
dotnet run
```

```powershell
$body = '{"destination":"xxx","events":[]}'
$hmac = New-Object System.Security.Cryptography.HMACSHA256
$hmac.Key = [Text.Encoding]::UTF8.GetBytes("demo-secret")
$sig = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body)))
Invoke-WebRequest http://localhost:5091/webhook -Method Post -Body $body `
    -ContentType 'application/json' -Headers @{ 'x-line-signature' = $sig }
# -> 200

Invoke-WebRequest http://localhost:5091/webhook -Method Post -Body $body `
    -ContentType 'application/json' -Headers @{ 'x-line-signature' = 'bogus' }
# -> 401
```

Both round-trips confirmed locally before moving on: a valid signature is accepted, a tampered one
is rejected. Connecting a real channel over a dev tunnel is covered in the end-to-end chapter.

## Chapter 3 — Pet state and the growth engine

**What we're building:** the pet simulation itself — `PetState`, `PetGrowthEngine`, and
`PetStore` — with no dependency on any LINE API at all. This is deliberately the one piece of the
app we unit-test: every other sample in `line-dotnet` has zero tests, but this is pure branching
logic (decay clamping, level thresholds, a hunger gate) with real edge cases, so it's cheap and
valuable to verify in isolation.

**Design decisions, and why:**

- **Lazy decay, no background timer.** `PetGrowthEngine.ApplyDecay` computes hunger/happiness loss
  from elapsed wall-clock time whenever the pet is touched (fed, played with, or just checked on).
  A `BackgroundService` ticking every few seconds to simulate decay would be simulation-for-its-own-
  sake — nothing observes the pet between interactions anyway.
- **No "death" mechanic.** If `play` is attempted while `Hunger <= 20`, it fails — a `PlayResult`
  with `Success = false` — but nothing is lost. The demo should never permanently punish a user for
  not checking in; the failure branch exists to show off error/branch handling, not to make the
  game harsher.
- **Level from a formula, not a table.** `Level = 1 + Xp / 50` — plain integer division. No lookup
  table, no curve. Three evolution stages (`Hatchling` / `Juvenile` / `Adult`) are bucketed
  straight from the level.

**The code** (`Services/PetGrowthEngine.cs`):

```csharp
public static PetState ApplyDecay(PetState state, DateTimeOffset now)
{
    var elapsedHours = Math.Max(0, (now - state.LastInteractionUtc).TotalHours);
    var hunger = Math.Max(0, state.Hunger - elapsedHours * HungerDecayPerHour);
    var happiness = Math.Max(0, state.Happiness - elapsedHours * HappinessDecayPerHour);
    return state with { Hunger = hunger, Happiness = happiness, LastInteractionUtc = now };
}

public static PlayResult Play(PetState state, DateTimeOffset now)
{
    var decayed = ApplyDecay(state, now);
    if (decayed.Hunger <= PlayHungerThreshold)
        return new PlayResult(decayed, Success: false);

    var played = decayed with { Happiness = Math.Min(100, decayed.Happiness + PlayHappinessGain), Xp = decayed.Xp + XpPerAction };
    return new PlayResult(played, Success: true);
}

public static int Level(PetState state) => 1 + state.Xp / XpPerLevel;
```

`PetStore` is a plain `ConcurrentDictionary<string, PetState>` wrapped in a singleton — no
`IPetStore` interface, since there's exactly one implementation and one caller. Matching every
other in-memory store in this app: no persistence, state resets on restart, and that's fine for a
demo.

**Try it:**

```powershell
dotnet test
```

```
成功!   -失敗:     0、合格:    16、スキップ:     0、合計:    16
```

16 tests covering decay clamping at zero, the feed/play gains clamped at 100, the hunger gate
blocking `play`, and the level→stage boundaries. Nothing here talks to LINE yet — that's next.

## Chapter 4 — Flex Message replies, and replacing the echo with postback dispatch

**What we're building:** `PetFlexMessageFactory`, and swapping the webhook handler's echo branch
for real pet-care dispatch — a `PostbackEvent` with `Data` of `"action=feed"` / `"action=play"` /
`"action=status"` now drives `PetGrowthEngine` and replies with a Flex Message status card.

**Why Flex Messages are hand-built:** `FlexBubble`/`FlexBox`/`FlexText` are plain generated POCOs —
`Line.OpenApi.Messaging` has no facade for assembling them (unlike, say, `RichMenuClient` for rich
menus). So `PetFlexMessageFactory` is the one place in this app that shape gets built by hand.

**Design choice — text as a progress bar, not images.** The status card renders Hunger/Happiness
as `"█████░░░░░ 50%"` text rather than pet artwork. Flex images need a publicly reachable HTTPS
URL, which would mean hosting image assets somewhere reachable from LINE's servers — a real
problem for a demo app that isn't worth solving just to make two stat bars. Text renders instantly
and needs no asset hosting.

**Design choice — one input surface.** The reply `FlexBubble` has no `Footer` and no quick-reply
buttons. All pet care happens through the rich menu (built next chapter); duplicating that with
Flex buttons would just be two ways to do the same thing.

**The code** (`Services/PetFlexMessageFactory.cs`):

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
`Success = false`.

The webhook handler (`Program.cs`) now dispatches on the postback's `Data` string, resolving the
LINE user ID from the event's `Source` (a `UserSource` carries `UserId`; other source types —
group, room — are skipped since this pet is per-user):

```csharp
if (ev is not PostbackEvent { ReplyToken: { Length: > 0 } replyToken } postback || messaging is null)
    continue;
if (postback.Source is not UserSource { UserId: { Length: > 0 } userId })
    continue;

var pet = petStore.GetOrCreate(userId, now);
FlexMessage reply = postback.Postback?.Data switch
{
    "action=feed" => /* PetGrowthEngine.Feed + BuildStatus */,
    "action=play" => /* PetGrowthEngine.Play + BuildStatus or BuildPlayRefused */,
    "action=status" => /* PetGrowthEngine.Status + BuildStatus */,
    _ => /* unrecognized postback data: skip */,
};
```

**Try it:** with no rich menu yet (that's next chapter), a postback event can still be simulated
directly — self-sign a payload with a `postback` event instead of a `message` event:

```powershell
$body = '{"destination":"xxx","events":[{"type":"postback","replyToken":"dummy","source":{"type":"user","userId":"U123"},"postback":{"data":"action=feed"},"timestamp":1,"mode":"active"}]}'
# ...sign and POST as in Chapter 2...
```

With a real channel access token configured, this triggers an actual reply attempt to
`api.line.me`; with a placeholder token (or no network access, as in a sandboxed dev environment)
the reply call fails and is logged, but the endpoint still returns `200` — the same
absorb-and-ack idiom from Chapter 2, now covering a real downstream call. Confirmed locally that
the request reaches the point of calling LINE's reply endpoint without throwing; wiring a real
token and rich menu together happens in the end-to-end chapter.

## Chapter 5 — Rich menu bootstrap (`dotnet run -- setup`)

**What we're building:** a one-shot CLI verb that creates the rich menu, uploads its image, and
sets it as the account's default — the missing piece that turns the postback data strings from
Chapter 4 (`"action=feed"` etc.) into something a user can actually tap.

**Why a CLI verb, not an HTTP endpoint.** Setting the *default* rich menu is account-wide — it
affects every user of the channel. That's an admin action, and this app is exposed to the internet
over a dev tunnel for the webhook to work. A `POST /setup` endpoint would put a destructive,
unauthenticated admin action on the same public surface as the webhook. Dispatching on
`args[0] == "setup"` *before* `WebApplication` is even built keeps it a local-only action —
matching the verb style already used by `Line.OpenApi.Samples.Console` (`dotnet run -- send`,
`dotnet run -- webhook`, etc.).

**The blocking prerequisite: an actual image file.** `RichMenuClient.SetImageFromFileAsync` needs
a real PNG on disk — there's no way around uploading actual pixels. This repo has no image-
generation library (adding one just to draw four colored boxes would be a disproportionate new
dependency), so the placeholder at `assets/richmenu.png` was generated once, out-of-band, with a
throwaway PowerShell + `System.Drawing` script (not part of the app — a build-time artifact only):

```powershell
Add-Type -AssemblyName System.Drawing
# ...draw four labeled 1250x843 quadrants (FEED / PLAY / STATUS / SHOP) on a 2500x1686 canvas...
$bmp.Save("assets/richmenu.png", [System.Drawing.Imaging.ImageFormat]::Png)
```

Replace this file with real artwork before using the app for anything beyond a demo.

**The code** (`Services/RichMenuBootstrapper.cs`):

```csharp
var request = new RichMenuRequest
{
    Name = "LineCompanionBot default menu",
    ChatBarText = "Menu",
    Selected = true,
    Size = new RichMenuSize { Width = 2500, Height = 1686 },
    Areas = new List<RichMenuArea>
    {
        Area(0, 0, "action=feed"),
        Area(HalfWidth, 0, "action=play"),
        Area(0, HalfHeight, "action=status"),
        AreaUri(HalfWidth, HalfHeight, shopUri), // the LIFF shop URL from Chapter 6
    },
};

var richMenu = RichMenuClient.CreateWithStaticToken(settings.ChannelAccessToken!);
var richMenuId = await richMenu.CreateAsync(request);
await richMenu.SetImageFromFileAsync(richMenuId!, imagePath);
await richMenu.SetDefaultAsync(richMenuId!);
```

Three of the four areas use `PostbackAction` (matching the `"action=feed"` / `"action=play"` /
`"action=status"` strings the webhook handler already dispatches on); the fourth uses `URIAction`
pointing at the MINI App shop's LIFF URL — the shop button doesn't need a postback at all, LINE
just opens the URL directly.

**Try it** (no real channel needed to confirm the CLI wiring itself):

```powershell
dotnet run -- setup
```

```
LINE_CHANNEL_ACCESS_TOKEN is not set — cannot create a rich menu.
```

Confirmed the verb dispatches before the web host starts and exits cleanly with a clear message
when unconfigured — no server boots, no crash. Running it against a real channel access token
(covered in the end-to-end chapter) actually creates and activates the menu.

## Chapter 6 — MINI App shop: front end and backend

**What we're building:** the shop the rich menu's Shop button opens — a plain HTML/JS page served
from `wwwroot/shop`, backed by three endpoints (`/api/shop/config`, `/api/shop/catalog`,
`/api/shop/reserve`) plus an inventory lookup.

**The request contract, and where each field comes from.** `ReserveProductAsync` needs a user
access token, the client's IP, and its OS — but nothing in that call chain yields a LINE user ID
for our own bookkeeping. The front end supplies what the backend can't derive on its own:

```js
const profile = await liff.getProfile();       // -> userId
const token = liff.getAccessToken();           // -> the user access token ReserveProductAsync needs
const os = liff.getOS();                       // -> "ios" | "android" (more reliable than UA sniffing)
```

These, plus `productId`, become the `/api/shop/reserve` request body. `clientIp` is filled in
server-side from `X-Forwarded-For` (present behind a dev tunnel) falling back to
`HttpContext.Connection.RemoteIpAddress`.

**The backend** (`Program.cs`):

```csharp
app.MapPost("/api/shop/reserve", async (ShopReserveRequest req, MiniAppClient miniApp, ...) =>
{
    var item = ShopCatalog.Find(req.ProductId);
    if (item is null) return Results.NotFound(...);

    // Best-effort — see Chapter 8 for why a failure here is never fatal to the purchase.
    try
    {
        var notifierToken = await miniApp.IssueNotificationTokenAsync(settings.ChannelAccessToken!, req.LiffAccessToken);
        if (notifierToken is not null) notifierTokens.Save(req.UserId, notifierToken);
    }
    catch (Exception ex) { app.Logger.LogWarning(ex, "..."); }

    var reserved = await miniApp.ReserveProductAsync(req.LiffAccessToken, clientIp, req.ClientOs ?? "android", item.ProductId, item.Name);
    orderStore.Record(reserved.OrderId!, req.UserId, item.ProductId);
    return Results.Ok(new { orderId = reserved.OrderId });
});
```

Two things worth calling out:

- **Notifier token issuance happens here, not at purchase-completion time.** `IssueNotificationTokenAsync`
  needs the LIFF access token, which only the front end has, at the moment the user is actively in
  the shop. The token gets stored now so `PurchaseReconciliationService` (Chapter 7) can use it
  later, when the purchase actually completes — which may be seconds to tens of seconds after this
  request returns.
- **The notifier call is wrapped in its own try/catch, separate from the reserve call.** If issuing
  a notifier token fails (very possible — see Chapter 8's caveat about token types), the purchase
  itself must still proceed. Only `ReserveProductAsync` failing should fail the request.

**The blocking TODO, left deliberately unfilled.** After `reserve` returns an `orderId`, the front
end is supposed to hand it to LINE's in-app purchase JS SDK to actually drive the purchase UI.
That SDK isn't something `Line.OpenApi.*` wraps — it's client-side, MINI-App-specific JavaScript,
and guessing its method name would be worse than leaving a clearly marked gap:

```js
// TODO: hand `orderId` to LINE's in-app purchase SDK to actually complete the transaction.
// The exact call is outside Line.OpenApi.*'s scope — verify it against LINE's official MINI App
// IAP docs before wiring this up for real. Do not guess it.
```

**Try it** (no MINI App channel needed yet to exercise the backend contract):

```powershell
$env:LINE_MINIAPP_LIFF_ID = "1234567890-abcdefgh"   # any placeholder value
dotnet run
```

```powershell
Invoke-RestMethod http://localhost:5091/api/shop/catalog
# -> the 3-item catalog (Golden Kibble / Party Hat / Star Badge)

Invoke-RestMethod http://localhost:5091/api/shop/reserve -Method Post -ContentType 'application/json' -Body '{}'
# -> 400 (missing required fields)

Invoke-RestMethod http://localhost:5091/api/shop/reserve -Method Post -ContentType 'application/json' `
    -Body '{"userId":"U1","productId":"unknown-item","liffAccessToken":"fake"}'
# -> 404 (unknown productId)
```

Confirmed the catalog, config, and inventory endpoints all respond correctly, and the reserve
endpoint's validation branches (missing fields → 400, unknown product → 404) fire before any
network call — with a real MINI App channel and a real LIFF access token, the same request goes on
to actually call `ReserveProductAsync`, covered in the end-to-end chapter.

## Chapter 7 — Purchase reconciliation

**What we're building:** `PurchaseReconciliationService`, a `BackgroundService` that discovers
completed purchases and grants the corresponding item — the piece that closes the loop opened in
Chapter 6's `reserve` call.

**Why polling, and why it's the one genuinely awkward part of this whole system.** `MiniAppClient`
has no push webhook for IAP events (unlike the Messaging webhook from Chapter 2) — `GetWebhookEventsAsync`
is a pull API over a 7-day window, cursor-paginated. So instead of reacting instantly, this service
ticks on a timer (`LINE_MINIAPP_POLL_SECONDS`, default 30) and asks "what happened since I last
checked?"

**The polling loop** (`Services/PurchaseReconciliationService.cs`):

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    if (!_settings.HasMessaging) { /* log and return — no token, nothing to poll with */ }

    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.PollSeconds));
    do
    {
        try { await PollOnceAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogWarning(ex, "Purchase reconciliation poll failed; will retry next tick."); }
    } while (await timer.WaitForNextTickAsync(stoppingToken));
}
```

A poll failure is swallowed and retried next tick — the same "absorb and continue" idiom as the
webhook handler, applied to a background loop instead of an HTTP response.

**Each poll walks every page in the window before advancing the watermark:**

```csharp
private async Task PollOnceAsync(CancellationToken ct)
{
    var start = _watermarkEpochSeconds;
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string? cursor = null;

    do
    {
        var page = await _miniApp.GetWebhookEventsAsync(
            _settings.ChannelAccessToken!, start, now, pageSize: 50, cursor: cursor, status: "SUCCESS", ct);

        foreach (var entry in page?.Events ?? new())
        {
            var ev = entry.Event;
            if (ev?.OrderId is null || !_orders.TryGet(ev.OrderId, out var order)) continue;

            if (ev.Type == "purchaseComplete" && _inventory.Grant(order.UserId, order.OrderId, order.ProductId))
                _logger.LogInformation("Granted {ProductId} to {UserId} (order {OrderId}).", ...);
            else if (ev.Type == "refundComplete")
                _inventory.Revoke(order.UserId, order.OrderId);
        }

        cursor = page?.NextCursor;
    } while (!string.IsNullOrEmpty(cursor));

    _watermarkEpochSeconds = now; // only after every page in the window succeeded
}
```

Three design decisions worth calling out:

- **Only orders `OrderStore` recognizes are acted on.** `MiniAppWebhookEvent` already carries
  `UserId` and `ProductId` directly, so `OrderStore` isn't strictly needed to *resolve* the user —
  but it's still consulted as a gate, so this app only grants items for purchases it itself
  initiated via `reserve`, not any other IAP activity that might exist on the same channel.
- **Idempotent by construction, not by tracking "already processed" separately.**
  `InventoryStore.Grant` keys off `OrderId` and simply no-ops on a repeat — so if the app restarts
  mid-window and re-scans an overlapping range, nothing gets double-granted. No separate
  "processed events" set is needed.
- **The watermark advances only after every page succeeds.** Advancing per-event would risk a
  silent gap if the loop were interrupted partway through a page.

**Refunds are handled symmetrically** — `refundComplete` revokes the item via the same `OrderId`.
Small amount of code, and it demonstrates the field exists in the response shape at all.

**Try it:** with no channel access token, the service logs that it's disabled and does nothing —
confirmed the app stays healthy either way:

```
info: LineCompanionBot.Services.PurchaseReconciliationService[0]
      LINE_CHANNEL_ACCESS_TOKEN is not set — purchase reconciliation is disabled.
```

With a token configured, the service reaches the same network boundary already seen in Chapters 2
and 4 — in this sandboxed dev environment, outbound calls to `api.line.me` are blocked, so a poll
attempt fails and is logged, then retried next tick, exactly as designed. Verifying it actually
finds a real `purchaseComplete` event requires a live MINI App channel and a completed purchase,
covered in the end-to-end chapter — the notification that fires when a grant happens is wired up
next.

## Chapter 8 — Notifying the user: service message, with a push fallback

**What we're building:** `NotifyPurchaseAsync`, called right after `InventoryStore.Grant` succeeds
in Chapter 7's poll loop — the step that actually tells the user in chat that they got their item.

**A DI subtlety worth calling out first.** `MessagingClient` is only registered in the container
when `LINE_CHANNEL_ACCESS_TOKEN` is set (Chapter 1's gating). `PurchaseReconciliationService`
*also* only runs when that token is set — but a `BackgroundService`'s constructor dependencies are
resolved eagerly by the host at startup, regardless of what its `ExecuteAsync` later decides to
do. Taking `MessagingClient` directly as a constructor parameter would make the host crash at
startup whenever the token is unset, even though `ExecuteAsync` would have returned immediately
anyway. The fix: take `IServiceProvider` instead (always resolvable — it's framework-provided),
and resolve `MessagingClient` from it lazily, *inside* the already-token-gated `ExecuteAsync`:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    if (!_settings.HasMessaging) { /* log and return */ }

    var messaging = _serviceProvider.GetRequiredService<MessagingClient>(); // safe: token is set here
    // ...PollOnceAsync(messaging, ...) threads it through to NotifyPurchaseAsync
}
```

This is the ASP.NET Core minimal API convention (`[FromServices] MessagingClient?` on an endpoint
parameter, seen back in Chapter 2) hitting a wall in a hosted service: minimal-API parameter
binding tolerates an unregistered optional service by resolving at request time, but constructor
injection through `ActivatorUtilities` does not — it resolves everything up front. Confirmed by
running with `LINE_CHANNEL_ACCESS_TOKEN` set to a placeholder token: the host starts cleanly and
the health endpoint responds immediately, rather than the host crashing on an unresolvable
constructor dependency.

**The notification logic** (`Services/PurchaseReconciliationService.cs`):

```csharp
private async Task NotifyPurchaseAsync(ShopOrder order, MessagingClient messaging, CancellationToken ct)
{
    var itemName = ShopCatalog.Find(order.ProductId)?.Name ?? order.ProductId;

    if (_settings.TemplateName is not null
        && _notifierTokens.TryGet(order.UserId, out var token)
        && token.NotificationToken is not null)
    {
        try
        {
            var renewed = await _miniApp.SendServiceMessageAsync(
                _settings.ChannelAccessToken!, token.NotificationToken, _settings.TemplateName,
                new Dictionary<string, string> { ["itemName"] = itemName }, ct);
            if (renewed is not null) _notifierTokens.Save(order.UserId, renewed);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Service message failed for {UserId}; falling back to push.", order.UserId);
        }
    }

    try
    {
        await messaging.Api.V2.Bot.Message.Push.PostAsync(new PushMessageRequest
        {
            To = order.UserId,
            Messages = new List<Message> { new TextMessage { Text = $"You received: {itemName}!" } },
        }, cancellationToken: ct);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Push fallback notification failed for {UserId}.", order.UserId);
    }
}
```

**Why the gate is two conditions, not one.** It's tempting to gate purely on "is a template
configured" — but that alone doesn't guarantee a *usable* token exists for *this specific user*:
`IssueNotificationTokenAsync` (Chapter 6) only ran if that user actually opened the shop while a
LIFF access token was available, and a token is spent after 5 sends. So the check is
`TemplateName is not null && a live token exists for this user` — either half missing, or the
service message call throwing for any reason (most likely: this app's `LINE_CHANNEL_ACCESS_TOKEN`
is a long-lived token, but the notifier endpoints require a stateless/short-lived one — a real
constraint documented in `MiniAppClient`'s XML docs), falls through to the plain `Push` message,
which needs only the already-configured channel token and always works. In a fresh, unreviewed
demo environment, **the push fallback is the common path, not an edge case** — that's by design:
the point of showing off `SendServiceMessageAsync` is additive polish once its two prerequisites
are met, not a hard requirement for the demo to function.

**Try it:** already exercised in Chapter 7's log output — with no template configured
(`LINE_MINIAPP_TEMPLATE_NAME` unset, the default), any grant would skip straight to the push
branch. Seeing an actual grant fire requires a completed real purchase, covered next.

## Chapter 9 — End-to-end with a real channel, and troubleshooting

Every previous chapter verified its piece locally — signature round-trips, postback dispatch,
Flex Message construction, the CLI verb, the shop's HTTP contract, the poll-and-retry loop, all
confirmed without needing a live LINE channel. This chapter is what's left: wiring a real channel
so all of it runs together, and what tends to go wrong when you do.

### Console setup, in the order that avoids dead ends

1. **Create a Messaging API channel** in the [LINE Developers Console](https://developers.line.biz/console/).
   Note its **channel secret** (→ `LINE_CHANNEL_SECRET`) and issue a **channel access token**
   (→ `LINE_CHANNEL_ACCESS_TOKEN`).
2. **Create a LINE MINI App channel** under the same provider. This is a distinct product from a
   regular LIFF app and has its own review/trial-user flow — add yourself as a **trial user** so
   you can test without going through full review. Note the **LIFF ID** it assigns
   (→ `LINE_MINIAPP_LIFF_ID`).
3. Getting this order wrong (e.g. trying to register a MINI App channel before understanding it
   needs its own provider setup, distinct from the Messaging API channel) is the most likely
   real-world stumbling block here — more so than anything in the code.

### Bringing it up

```powershell
cd src/LineCompanionBot
$env:LINE_CHANNEL_SECRET       = "<channel secret>"
$env:LINE_CHANNEL_ACCESS_TOKEN = "<channel access token>"
$env:LINE_MINIAPP_LIFF_ID      = "<liff id>"

dotnet run -- setup   # creates and activates the rich menu — do this once
dotnet run             # starts the app
```

Expose the app with a dev tunnel exactly as in the `Line.OpenApi.Samples.Webhook` sample:

```powershell
devtunnel user login       # first time only
devtunnel host -p 5091 --allow-anonymous
```

Set the forwarded HTTPS URL + `/webhook` as the channel's Webhook URL in the console, turn
**Use webhook** on, and click **Verify**.

### Trying the full loop

1. Add the bot as a friend; the rich menu (Feed / Play / Status / Shop quadrants) should appear
   immediately — that's `dotnet run -- setup` having taken effect.
2. Tap **Feed** / **Play** / **Status** — each should produce a Flex Message status card within
   about a second. Try **Play** while Hunger is low (or wait — decay is real-time) to see the
   refusal card.
3. Tap **Shop** to open the MINI App; it should load the catalog and let you reserve an item.
   Completing the actual purchase requires wiring the client-side IAP SDK call left as a TODO in
   `shop.js` (Chapter 6) — verify the exact call against LINE's current MINI App IAP docs first.
4. Once a purchase completes, `PurchaseReconciliationService` picks it up on its next poll tick
   (`LINE_MINIAPP_POLL_SECONDS`, default 30 — **not instant**, there is no push webhook for this)
   and a chat message announcing the new item should arrive.

### Troubleshooting

- **Rich menu doesn't appear / tapping it does nothing.** Confirm `dotnet run -- setup` printed
  a rich menu id (not the "not set" message). Check `GET /` reports `messaging: enabled`.
- **401 on `/webhook`.** `LINE_CHANNEL_SECRET` doesn't match the channel's. (Same failure mode as
  the plain `Line.OpenApi.Samples.Webhook` sample.)
- **Feed/Play/Status postback does nothing.** Check the app logs for "Failed to reply to a
  postback event" — usually an expired reply token (valid ~1 minute) from testing too slowly, or
  `LINE_CHANNEL_ACCESS_TOKEN` being unset/invalid.
- **Shop button opens a blank/broken page.** `LINE_MINIAPP_LIFF_ID` is unset or wrong, or the MINI
  App channel's endpoint URL isn't pointed at this app's `/shop/` path — this is a MINI App console
  configuration issue, not a code issue.
- **Purchase completes but no chat message arrives.** This is expected to take up to
  `LINE_MINIAPP_POLL_SECONDS` — there is no instant push for IAP completion. If it never arrives,
  check the logs for `PurchaseReconciliationService` warnings (an invalid/expired channel token is
  the most common cause).
- **Service message never sends, always falls back to push.** Expected unless both
  `LINE_MINIAPP_TEMPLATE_NAME` is set to an *approved* template and the user opened the shop
  recently enough for a live notifier token to exist — see Chapter 8. The push fallback is not a
  bug; it's the intended default-safe path.

### What's verified vs. what needs a live channel to fully confirm

Everything through Chapter 8 was confirmed running locally in this repo's own sandboxed dev
environment: signature verification (both accept and reject), postback dispatch into
`PetGrowthEngine` producing a real Flex Message, the `setup` CLI verb's dispatch and clean
no-token exit, all shop endpoints (config/catalog/inventory/reserve validation branches), and the
reconciliation service's poll-retry loop actually reaching `api.line.me` and handling a real `401`
response without crashing. What remains — an actual chat reply arriving, a real rich menu
rendering in the LINE app, and a completed IAP purchase triggering the full grant→notify path —
requires the live channel setup above, which is why this chapter exists separately rather than
folding "trust me, it works" into the earlier ones.

## Post-review refinements

This project follows the same 3-role review gate (code / security / test-arch) established in the
sibling `Line.OpenApi.*` library repo — the reviews came back **CONCERNS (non-blocking)** across
all three, and the actionable findings were fixed before considering the app done. Recorded here
rather than rewritten back into the chapters above, since these are refinements to already-working
code, not new features:

- **Reconciliation now trusts LINE's own event data over client input.** `PurchaseReconciliationService.PollOnceAsync`
  grants and notifies using `ev.UserId` (from LINE's own IAP webhook payload) rather than
  `order.UserId` (the client-supplied value recorded at `/api/shop/reserve` time), logging a
  warning if they ever mismatch. This was both a correctness fix (`MiniAppWebhookEvent` already
  carries the authoritative user id — no reason to prefer the unverified one) and the concrete
  mitigation for the security review's finding that `/api/shop/reserve` trusts a client-supplied
  `userId`: even if a caller lies about it, the actual grant and chat notification follow the real
  purchaser, not the caller's claim.
- **A small trailing buffer on the poll window** (`TrailingBufferSeconds = 5` in
  `PurchaseReconciliationService`) — querying right up to the current instant risked missing an
  event that completed moments ago but wasn't indexed yet on LINE's side. Costs nothing since
  Grant/Revoke are idempotent by `OrderId`.
- **`NotifyPurchaseAsync`'s fallback logic was tightened** so only the `SendServiceMessageAsync`
  call itself gates the push fallback — saving the renewed token no longer sits inside the same
  `try` as the send, which previously risked a duplicate push if bookkeeping (not sending) were to
  throw. If *both* the service message and the push fallback fail, that's now logged at `Error`
  level (not `Warning`) — it means an item was durably granted but the user was never told, and
  nothing else retries it.
- **`InventoryStore.Get` now snapshots under the same lock `Grant`/`Revoke` use** — it's reachable
  from `GET /api/shop/inventory/{userId}` concurrently with the background reconciliation loop
  mutating the same list, which is unsafe for a plain `List<T>` without synchronizing the read too.
- **"Golden Kibble" now actually does something.** The catalog always described it as refilling
  Hunger to full, but nothing consumed it or touched `PetStore` — a reviewer caught that a reader
  would buy it and see no effect. `InventoryStore.TryConsume` and `PetGrowthEngine.FeedRare` close
  the loop: feeding while holding an unconsumed Golden Kibble now consumes it for an instant full
  refill instead of the usual partial gain (cosmetic items intentionally still have no feed-time
  effect).
- **Two more `PetGrowthEngine` test cases** closed a gap the tutorial itself had claimed was
  covered but wasn't: `Play`'s happiness gain clamped at 100, and `FeedRare`'s full-refill behavior.
- **Documented, not code-fixed:** the `X-Forwarded-For` trust gap (no trusted-proxy validation) is
  called out in `Program.cs` and the README rather than solved — validating it properly needs a
  real reverse-proxy setup this demo doesn't have, and the field is only an anti-fraud signal to
  LINE, not a security boundary of this app's own.

## Persistence abstraction refactor

A later pass reworked how the four in-memory stores (`PetStore`, `OrderStore`, `InventoryStore`,
`NotifierTokenStore`) are exposed, so swapping the in-memory demo storage for a real database later
is a DI registration change, not a rewrite:

- **Each store is now behind an interface** (`IPetStore`, `IOrderStore`, `IInventoryStore`,
  `INotifierTokenStore`) under `src/LineCompanionBot/Persistence/`, with every method **async-shaped**
  (`Task`/`Task<T>`) even though the current `InMemory*` implementations never actually await
  anything. This matters because an interface can't cheaply grow a `CancellationToken` or switch a
  sync method to async later without touching every call site — shaping it for real I/O from the
  start means an EF Core/Dapper-backed implementation drops in without changing any caller.
- **One seam, not four.** `AddInMemoryPersistence()` (in
  `Persistence/InMemory/PersistenceServiceCollectionExtensions.cs`) registers all four
  implementations in a single call from `Program.cs`. A real deployment swaps that one line for,
  e.g., `AddSqlPersistence(connectionString)` — every consumer depends on the `I*Store` interfaces,
  never on the concrete `InMemory*` types.
- **`PurchaseReconciliationService` no longer takes stores as constructor dependencies.** It's a
  Singleton `BackgroundService` for the process's whole lifetime; the in-memory stores only happen
  to be Singleton too today. An RDB-backed store would typically be registered Scoped (one
  `DbContext` per unit of work), and a Singleton can't hold a Scoped dependency directly (the
  "captive dependency" problem — it would pin the first-ever `DbContext` for the app's entire
  lifetime). The service now takes an `IServiceScopeFactory` and resolves `IOrderStore`,
  `IInventoryStore`, `INotifierTokenStore`, and the LINE clients from a fresh `IServiceProvider`
  scope inside `PollOnceAsync`, created and disposed once per poll tick. Swapping store lifetimes
  later needs no change here.
- **`bool TryGet(..., out T value)` became `Task<T?> TryGetAsync(...)`.** C# doesn't allow `out`
  parameters on `async` methods, so making these stores async-shaped forced a cleaner nullable-return
  pattern anyway — which also fixes a pre-existing nullability lie (`out ShopOrder order` was
  annotated non-nullable but `TryGetValue` populates it with `default` on a miss).
- **`CancellationToken` now flows from the request into the store/webhook calls** in the `/webhook`
  and `/api/shop/reserve` handlers (bound automatically from `HttpContext.RequestAborted` — no
  `[FromServices]` needed for a `CancellationToken` parameter). `MiniAppClient`'s
  `IssueNotificationTokenAsync`/`ReserveProductAsync` don't expose a cancellation overload, so the
  token only reaches the calls that support it.
- **`[FromServices]` was dropped from the endpoint parameters that resolve unconditionally
  registered services** (`IPetStore`, `IInventoryStore`, `IOrderStore`, `INotifierTokenStore`,
  `MiniAppClient`) — ASP.NET Core's minimal-API parameter binding infers "this is a DI service, not
  a route/body value" automatically once it's actually registered at startup, so the attribute was
  redundant ceremony there. It's still required on `WebhookRequestParser?`/`MessagingClient?` in the
  `/webhook` handler, though — those two are *conditionally* registered (only when
  `LINE_CHANNEL_SECRET`/`LINE_CHANNEL_ACCESS_TOKEN` are set), and the inference only recognizes types
  it can see registered at startup. Without the attribute, ASP.NET Core mis-binds one of them as a
  request-body parameter and the route fails to build at all — this was caught by re-running the
  app after the cleanup, not by the compiler.
