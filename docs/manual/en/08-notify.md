[← Chapter 7](07-reconciliation.md) | [Index](README.md) | [Chapter 9 →](09-end-to-end.md)

# Chapter 8 — Notifying the user: service message, with a push fallback

**What we're building:** `NotifyPurchaseAsync`, called right after `GrantAsync` succeeds in Chapter
7's poll loop — the step that actually tells the user in chat that they got their item.

**The strategy:** prefer a **service message** (richer, branded, template-based) but fall back to a
plain **push** whenever the service-message path isn't fully available — which, in a fresh demo
environment, is the *common* path, not an edge case. That's by design: `SendServiceMessageAsync` is
additive polish once its prerequisites are met, not a hard requirement for the demo to function.

**A DI note, already handled by Chapter 7.** `MessagingClient` is only registered when
`LINE_CHANNEL_ACCESS_TOKEN` is set. Because the service resolves it from the per-poll DI scope
(Chapter 7's `IServiceScopeFactory`) — and only ever polls when `HasMessaging` is true — it's never
resolved in the unconfigured case. Taking `MessagingClient` as a *constructor* dependency instead
would make the host crash at startup whenever the token is unset, since constructor injection
resolves eagerly; resolving inside the already-gated poll avoids that.

## The notification logic

```csharp
private async Task NotifyPurchaseAsync(
    string userId, string productId, MiniAppClient miniApp, MessagingClient messaging,
    INotifierTokenStore notifierTokens, CancellationToken ct)
{
    var itemName = ShopCatalog.Find(productId)?.Name ?? productId;

    var token = _settings.TemplateName is not null ? await notifierTokens.TryGetAsync(userId, ct) : null;
    if (token?.NotificationToken is not null)
    {
        // Only the send call itself gates the fallback — bookkeeping after a successful send (saving
        // the renewed token) must never cause a duplicate push if it were to throw.
        NotifierToken? renewed = null;
        try
        {
            renewed = await miniApp.SendServiceMessageAsync(
                _settings.ChannelAccessToken!, token.NotificationToken, _settings.TemplateName!,
                new Dictionary<string, string> { ["itemName"] = itemName }, ct);
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
            Messages = new List<Message> { new TextMessage { Text = $"You received: {itemName}!" } },
        }, cancellationToken: ct);
    }
    catch (Exception ex)
    {
        // Both paths failed: the item is durably granted (Chapter 7's idempotency) but the user was
        // never told, and nothing retries this — surface it loudly, not at Warning.
        _logger.LogError(ex, "Both service message and push fallback failed for {UserId} — item granted but never announced.", userId);
    }
}
```

## Why the details are the way they are

- **The gate is two conditions, not one.** It's tempting to gate purely on "is a template
  configured" — but that doesn't guarantee a *usable* token for *this specific user*.
  `IssueNotificationTokenAsync` (Chapter 6) only ran if the user opened the shop while a LIFF token
  was available, and a notifier token is spent after a handful of sends. So the check is
  `TemplateName is not null` **and** `a live token exists for this user`. Either half missing — or
  the send throwing for any reason — falls through to push.
- **The most likely reason the send throws** is that this app's `LINE_CHANNEL_ACCESS_TOKEN` is a
  long-lived token, but the notifier endpoints require a stateless/short-lived one (a real constraint
  documented in `MiniAppClient`'s XML docs). So push is the default path here, and that's fine.
- **Saving the renewed token sits *outside* the send's `try`.** An earlier version had it inside, so
  a failure in bookkeeping (not sending) could fall through to push after a message had *already*
  been sent — a duplicate notification. Splitting them means only the send gates the fallback.
- **Double failure logs at `Error`, not `Warning`.** If both service message and push fail, an item
  was granted but the user was never told and nothing else retries it — that's an operator-visible
  problem, not a routine hiccup.

## Try it

Already exercised by Chapter 7's log output: with no template configured
(`LINE_MINIAPP_TEMPLATE_NAME` unset, the default), any grant skips straight to the push branch.
Seeing an actual notification fire requires a completed real purchase — which brings everything
together in the final chapter.
