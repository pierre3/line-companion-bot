[← Chapter 1](01-project-skeleton.md) | [Index](README.md) | [Chapter 3 →](03-pet-growth-engine.md)

# Chapter 2 — Webhook receive + signature verification

**What we're building:** `POST /webhook` — verify LINE's HMAC-SHA256 signature over the raw request
body, parse the payload, and (for now) echo text messages back. [Chapter 4](04-flex-postback.md)
replaces the echo branch with real pet-care dispatch; starting from a known-working echo means
you're only debugging one thing at a time.

**Where it lives:** in its own file from the start — `Endpoints/WebhookEndpoints.cs`, exposed as a
`MapWebhookEndpoint()` extension method. Minimal APIs recommend pulling substantial handlers out of
`Program.cs` once there's more than a trivial one, and this app ends up with two such handlers
(webhook here, shop in [Chapter 6](06-shop.md)); building them in their final home now avoids a
move later.

## The handler

Create `src/LineCompanionBot/Endpoints/WebhookEndpoints.cs`:

```csharp
using Line.OpenApi.Messaging;
using Line.OpenApi.Messaging.Generated.Api.Models;
using Line.OpenApi.Messaging.Webhook;
using Line.OpenApi.Messaging.Webhook.Generated.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LineCompanionBot.Endpoints;

public static class WebhookEndpoints
{
    public static void MapWebhookEndpoint(this WebApplication app)
    {
        app.MapPost("/webhook", async (
            HttpRequest request,
            [FromServices] WebhookRequestParser? parser,
            [FromServices] MessagingClient? messaging,
            CancellationToken ct) =>
        {
            if (parser is null)
                return Results.Problem("LINE_CHANNEL_SECRET is not configured.", statusCode: 503);

            // The signature is computed over these exact bytes, so read them before any model binding.
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, ct);
            var body = ms.ToArray();
            var signature = request.Headers["x-line-signature"];

            CallbackRequest callback;
            try { callback = await parser.ParseAsync(body, signature); }
            catch (WebhookSignatureException) { return Results.Unauthorized(); }
            catch (WebhookPayloadException) { return Results.BadRequest(); }

            foreach (var ev in callback.Events ?? new())
            {
                // Chapter 4 replaces this echo branch with postback dispatch into the pet engine.
                if (ev is MessageEvent { Message: TextMessageContent text, ReplyToken: { Length: > 0 } replyToken }
                    && messaging is not null)
                {
                    try
                    {
                        await messaging.Api.V2.Bot.Message.Reply.PostAsync(new ReplyMessageRequest
                        {
                            ReplyToken = replyToken,
                            Messages = new List<Message> { new TextMessage { Text = $"echo: {text.Text}" } },
                        }, cancellationToken: ct);
                    }
                    catch (Exception ex) { app.Logger.LogWarning(ex, "Failed to reply."); }
                }
            }

            // Always 200 quickly: LINE retries any non-2xx response, which would duplicate deliveries.
            return Results.Ok();
        });
    }
}
```

Wire it up in `Program.cs`, after the health endpoint:

```csharp
app.MapWebhookEndpoint();
```

`MapWebhookEndpoint` is an extension method in `LineCompanionBot.Endpoints`, so add
`using LineCompanionBot.Endpoints;` to the top of `Program.cs` — Chapter 1's reduced `using` block
didn't reference it yet.

Three points that matter here and recur throughout the app:

- **`[FromServices]` is required, not decorative, on `parser` and `messaging`.** Both are
  *conditionally* registered (the `HasWebhook` / `HasMessaging` gates from Chapter 1). ASP.NET
  Core's automatic "is this a DI service or a body/route value?" inference only recognizes types it
  can see registered at startup — for a conditionally-registered type it guesses "body", and the
  route fails to build at all. The attribute forces the DI interpretation. (Contrast the
  *unconditionally* registered services in later chapters, where the attribute is correctly omitted.)
- **Read the raw bytes before anything else.** The HMAC is over the exact request body; letting the
  framework model-bind first would consume the stream and change the bytes.
- **Absorb the reply failure, still return 200.** A reply can fail — most commonly an expired reply
  token (valid ~1 minute). Because LINE retries any non-2xx delivery, turning a reply failure into a
  non-2xx would create a duplicate-delivery storm. Log and ack.

## Try it — no LINE channel needed

You can self-sign a payload exactly like LINE does. Set a throwaway secret in user-secrets so the
webhook registration activates, then F5:

```powershell
dotnet user-secrets set LINE_CHANNEL_SECRET "demo-secret" --project src/LineCompanionBot
```

With the app running, from a terminal:

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

A valid signature is accepted, a tampered one rejected — both confirmed locally. Set a breakpoint in
the handler and re-send to watch the parse succeed under the debugger. Connecting a real channel
over a dev tunnel is [Chapter 9](09-end-to-end.md).

> **Tip:** `GET /` should now report `webhook: enabled`. To go back to the unconfigured state,
> `dotnet user-secrets remove LINE_CHANNEL_SECRET --project src/LineCompanionBot`.
