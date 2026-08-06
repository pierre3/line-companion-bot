[← 第7章](07-reconciliation.md) | [索引](README.md) | [第9章 →](09-end-to-end.md)

# 第8章 — ユーザーへの通知: サービスメッセージ、Pushフォールバック付き

**このステップで作るもの:** `NotifyPurchaseAsync` です。第7章のポーリングループで `GrantAsync` が
成功した、ちょうどその直後に呼ばれ、アイテムを受け取ったことをチャットで実際にユーザーへ伝える
——第7章で付与まで漕ぎ着けた流れを、ここで人の目に見える形にします。

**戦略:** 基本は **サービスメッセージ**（よりリッチで、ブランド付き、テンプレートベース）を優先し、
その経路が完全には使えないときは常に素の **Push** へフォールバックする、という二段構えです。とはいえ
真新しいデモ環境では、このフォールバックはエッジケースというより、むしろ *ありふれた* 経路になります。
そしてこれは意図した設計です。というのも `SendServiceMessageAsync` は前提条件が揃って初めて効いてくる
付加的な仕上げであって、デモが機能するための必須要件ではないからです。

**DIに関する注記——第7章で既に手当て済み。** さて、`MessagingClient` は `LINE_CHANNEL_ACCESS_TOKEN`
が設定されているときだけ登録されます。このサービスはそれをポーリングごとのDIスコープ（第7章の
`IServiceScopeFactory`）から解決し、しかも `HasMessaging` が true のときにしかポーリングしないので、
未設定のケースでは決して解決されません。ここが勘どころで、仮に `MessagingClient` を *コンストラクタ*
依存として受け取ってしまうと、コンストラクタインジェクションは即座に解決してしまうため、トークンが
未設定のたびにホストが起動時にクラッシュしてしまいます。すでにゲートを通ったポーリングの内側で
解決することで、それを避けているわけです。

## 通知ロジック

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

## なぜ細部がこうなっているのか

- **ゲートは1条件ではなく2条件。** つい「テンプレートが設定されているか」だけでゲートしたく
  なるところですが、それでは *この特定のユーザー* に対する *使える* トークンの存在までは保証されません。
  というのも `IssueNotificationTokenAsync`（第6章）は、ユーザーがLIFFトークンの利用可能な状態で
  ショップを開いたときにしか走らず、notifierトークンは数回の送信で使い切られてしまうからです。そこで
  チェックは `TemplateName is not null` **かつ** *このユーザーに対する有効なトークンが存在すること* の
  二本立てにしています。どちらか一方でも欠けていれば——あるいは送信が何らかの理由で例外を投げれば
  ——素直にPushへ落ちます。
- **送信が例外を投げる、いちばんありそうな理由。** それは、このアプリの `LINE_CHANNEL_ACCESS_TOKEN`
  が長期トークンであるのに対し、notifier系エンドポイントは stateless/short-lived なトークンを要求する
  ことです（`MiniAppClient` のXMLドキュメントに明記された、実際の制約です）。ですからここではPushが
  むしろ既定の経路であり、それで問題ありません。
- **更新されたトークンの保存は、送信の `try` の *外側* に置く。** 実は以前のバージョンでは内側にあり、
  （送信そのものではなく）記録処理での失敗が、メッセージを *すでに* 送り終えた後でPushへ落ちてしまう
  ——つまり通知の重複——という恐れがありました。両者を分けたことで、フォールバックをゲートするのは
  送信だけになります。
- **二重失敗は `Warning` ではなく `Error` でログする。** サービスメッセージとPushの両方が失敗した
  ときは、アイテムは付与されたのにユーザーには何も伝わらず、しかも他に再試行してくれるものもありません
  ——これは日常的なつまずきではなく、運用者がきちんと気づくべき問題だからです。

## 動かしてみる

実はもう第7章のログ出力で試せています。テンプレートが未設定（`LINE_MINIAPP_TEMPLATE_NAME` が未設定、
つまりデフォルト）であれば、どの付与も直接Pushブランチへ飛んでいきます。とはいえ実際の通知が発火する
様子を見届けるには、完了した本物の購入が要ります——そのすべてが、いよいよ最終章で1つにまとまります。
