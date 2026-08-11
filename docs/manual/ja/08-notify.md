[← 第7章](07-reconciliation.md) | [索引](README.md) | [第9章 →](09-end-to-end.md)

# 第8章 — ユーザーへの通知: サービスメッセージ、Pushフォールバック付き

**このステップで作るもの:** `NotifyPurchaseAsync` です。第7章のポーリングループで `GrantAsync` が
成功した直後に呼ばれ、アイテムを受け取ったことをチャットでユーザーへ伝えます。第7章で付与まで
漕ぎ着けた流れを、ここで人の目に見える形にします。

**戦略:** 基本は **サービスメッセージ**（よりリッチで、ブランド付き、テンプレートベース）を優先し、
その経路が完全には使えないときは常に素の **Push** へフォールバックする、という二段構えです。真新しい
デモ環境では、このフォールバックはエッジケースというより、むしろ *ありふれた* 経路になります。
これは意図した設計です。`SendServiceMessageAsync` は前提条件が揃って初めて効いてくる付加的な仕上げ
であって、デモが機能するための必須要件ではないからです。

**DIに関する注記: 第7章で既に手当て済み。** `MessagingClient` は `LINE_CHANNEL_ACCESS_TOKEN`
が設定されているときだけ登録されます。このサービスはそれをポーリングごとのDIスコープ（第7章の
`IServiceScopeFactory`）から解決し、しかも `HasMessaging` が true のときにしかポーリングしないので、
未設定のケースでは決して解決されません。ここが勘どころです。仮に `MessagingClient` を *コンストラクタ*
依存として受け取ってしまうと、コンストラクタインジェクションは即座に解決するため、トークンが
未設定のたびにホストが起動時にクラッシュしてしまいます。すでにゲートを通ったポーリングの内側で
解決することで、それを避けています。

## 通知ロジック

第7章で `NotifyPurchaseAsync` は、サービスをビルドできるようにするためのスタブ（`=> Task.CompletedTask`）
でした。そのスタブメソッドを、下記の実装で丸ごと置き換えます。`PushMessageRequest`/`Message`/`TextMessage`
（`Line.OpenApi.Messaging.Generated.Api.Models`）と `NotifierToken`（`Line.OpenApi.MiniApp.Models`）を
使うので、先にファイル冒頭へ using を2本足します:

```csharp
using Line.OpenApi.Messaging.Generated.Api.Models;
using Line.OpenApi.MiniApp.Models;
```

```csharp
private async Task NotifyPurchaseAsync(
    string userId, string productId, MiniAppClient miniApp, MessagingClient messaging,
    INotifierTokenStore notifierTokens, CancellationToken ct)
{
    var itemName = ShopCatalog.Find(productId)?.Name ?? productId;

    var token = _settings.TemplateName is not null ? await notifierTokens.TryGetAsync(userId, ct) : null;
    if (token?.NotificationToken is not null)
    {
        // フォールバックをゲートするのは送信呼び出しだけ。送信成功後の記録処理（更新されたトークンの
        // 保存）が失敗しても、push を重複させてはならない。
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
            Messages = new List<Message> { new TextMessage { Type = "text", Text = $"You received: {itemName}!" } },
        }, cancellationToken: ct);
    }
    catch (Exception ex)
    {
        // 通知の両経路が失敗。アイテムは確実に付与されている（第7章の冪等性の保証）が、ユーザーには
        // 伝わっていない。これを再試行するものは他に無いので、Warning ではなく目立つレベルで表面化する。
        _logger.LogError(ex, "Both service message and push fallback failed for {UserId} — item was granted but never announced.", userId);
    }
}
```

## なぜ細部がこうなっているのか

- **ゲートは1条件ではなく2条件。** つい「テンプレートが設定されているか」だけでゲートしたく
  なりますが、それでは *この特定のユーザー* に対する *使える* トークンの存在までは保証されません。
  `IssueNotificationTokenAsync`（第6章）は、ユーザーがLIFFトークンの利用可能な状態でショップを
  開いたときにしか走らず、notifierトークンは数回の送信で使い切られてしまうからです。そこでチェックは
  `TemplateName is not null` **かつ** *このユーザーに対する有効なトークンが存在すること* の
  二本立てにしています。どちらか一方でも欠けていれば、あるいは送信が何らかの理由で例外を投げれば、
  素直にPushへ落ちます。
- **送信が例外を投げる、いちばんありそうな理由。** それは、このアプリの `LINE_CHANNEL_ACCESS_TOKEN`
  が長期トークンであるのに対し、notifier系エンドポイントは stateless/short-lived なトークンを要求する
  ことです（`MiniAppClient` のXMLドキュメントに明記された、実際の制約です）。ですからここではPushが
  むしろ既定の経路であり、それで問題ありません。
- **更新されたトークンの保存は、送信の `try` の *外側* に置く。** 以前のバージョンでは内側にあり、
  （送信そのものではなく）記録処理での失敗が、メッセージを *すでに* 送り終えた後でPushへ落ちてしまう、
  つまり通知の重複を招く恐れがありました。両者を分けたことで、フォールバックをゲートするのは
  送信だけになります。
- **二重失敗は `Warning` ではなく `Error` でログする。** サービスメッセージとPushの両方が失敗した
  ときは、アイテムは付与されたのにユーザーには何も伝わらず、しかも他に再試行してくれるものもありません。
  これは日常的なつまずきではなく、運用者がきちんと気づくべき問題だからです。

## 動かしてみる

実はもう第7章のログ出力で試せています。テンプレートが未設定（`LINE_MINIAPP_TEMPLATE_NAME` が未設定、
つまりデフォルト）であれば、どの付与も直接Pushブランチへ飛びます。ただし実際の通知が発火する様子を
見届けるには、完了した本物の購入が要ります。そのすべてが、いよいよ最終章で1つにまとまります。
