[← 第1章](01-project-skeleton.md) | [索引](README.md) | [第3章 →](03-pet-growth-engine.md)

# 第2章 — Webhook受信 + 署名検証

**このステップで作るもの:** `POST /webhook` です。生のリクエストボディに対するLINEのHMAC-SHA256署名を
検証し、ペイロードをパースして、今はまだテキストメッセージをそのままオウム返しします。このオウム返し
分岐は、[第4章](04-flex-postback.md)で実際の相棒の世話分岐へ置き換えます。最初から本番のロジックを
書かないのは、動作確認済みのオウム返しから始めれば、一度に一つのことだけをデバッグすればよくなるから
です。

**どこに置くか:** 最初から専用ファイル `Endpoints/WebhookEndpoints.cs` に置き、`MapWebhookEndpoint()`
拡張メソッドとして公開します。Minimal APIでは、ハンドラが単純でなくなってきたら `Program.cs` から
切り出すことが推奨されているためです。このアプリも最終的にはこうしたハンドラを2つ持つことになります
（ここでのwebhookと、[第6章](06-shop.md)のshop）。それなら、今のうちから最終的な置き場所で組み立てて
おくほうが、後で移動する手間がかかりません。

## ハンドラ

`src/LineCompanionBot/Endpoints/WebhookEndpoints.cs` を作成します:

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

            // 署名はこの生バイト列そのものに対して計算されるので、モデルバインディングの前に読む。
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
                // 第4章で、このオウム返し分岐を pet エンジンへの postback ディスパッチに置き換える。
                if (ev is MessageEvent { Message: TextMessageContent text, ReplyToken: { Length: > 0 } replyToken }
                    && messaging is not null)
                {
                    try
                    {
                        await messaging.Api.V2.Bot.Message.Reply.PostAsync(new ReplyMessageRequest
                        {
                            ReplyToken = replyToken,
                            Messages = new List<Message> { new TextMessage { Type = "text", Text = $"echo: {text.Text}" } },
                        }, cancellationToken: ct);
                    }
                    catch (Exception ex) { app.Logger.LogWarning(ex, "Failed to reply."); }
                }
            }

            // 常にすぐ 200 を返す。LINE は非 2xx をリトライするので、そのままだと重複配信を招く。
            return Results.Ok();
        });
    }
}
```

`Program.cs` のヘルスエンドポイントの後に組み込みます:

```csharp
app.MapWebhookEndpoint();
```

`MapWebhookEndpoint` は `LineCompanionBot.Endpoints` にある拡張メソッドなので、`Program.cs` の冒頭に
`using LineCompanionBot.Endpoints;` を追加してください。第1章の縮約した `using` ブロックでは、まだ
これを参照していませんでした。

ここで押さえておきたい、このアプリ全体で繰り返し出てくるポイントがいくつかあります:

- **`parser` と `messaging` の `[FromServices]` は必須です。** どちらも条件付きで登録される
  （第1章の `HasWebhook` / `HasMessaging` ゲート）からです。ASP.NET Coreは引数を「DIサービスか、
  ボディ/ルート値か」を自動で推論しますが、起動時に登録されているのが見える型しか認識しません。
  条件付きで登録される型は「ボディ」と推測され、そのままではルートの構築自体に失敗します。この属性で
  DIからの解決を明示するわけです。（後の章で登場する無条件に登録されるサービスでは、この属性を正しく
  省略しています。）
- **何よりも先に生のバイト列を読む。** HMACはリクエストボディそのものに対して計算されるからです。
  フレームワークに先にモデルバインディングをさせると、ストリームが消費され、手元に残るバイト列が
  変わってしまいます。
- **返信が失敗してもログに残すだけで、200は返す。** 返信はときに失敗します。多いのはリプライトークンの
  期限切れ（有効期間は約1分）です。LINEは非2xx応答をリトライするので、返信失敗をそのまま非2xxで返すと
  重複配信を招きます。そこで例外を捕まえてログに残し、それでも200を返します。
- **メッセージ POCO には必ず `type` 判別子を設定する。** `new TextMessage { Type = "text", … }` に
  注目してください。これらの生成モデルは `type` を初期値なしで持ち、設定されたときだけシリアライズ
  するため、省くと LINE がボディを `400` で弾きます。[第4章](04-flex-postback.md)の Flex コンポーネント
  も同様の対応が必要です（`"flex"`/`"bubble"`/`"box"`/`"text"`）。

## 試してみる — LINEチャネル不要

LINEと同じ方式で、ペイロードを自分で署名できます。まずはwebhook登録が有効になるよう、使い捨ての
シークレットをuser-secretsに設定してから、F5で起動しましょう:

```powershell
dotnet user-secrets set LINE_CHANNEL_SECRET "demo-secret" --project src/LineCompanionBot
```

アプリを起動した状態で、ターミナルから:

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

正しい署名は受理され、改ざんされた署名は拒否されます。どちらもLINEチャネル無しで、手元だけで
確かめられます。ハンドラにブレークポイントを置いて再送し、デバッガでパースが成功していく様子を
見てみてください。実チャネルをdev tunnel経由で繋ぐ手順は、[第9章](09-end-to-end.md)で扱います。

> **ヒント:** `GET /` が `webhook: enabled` と報告するようになっているはずです。未設定の状態に戻したく
> なったら、`dotnet user-secrets remove LINE_CHANNEL_SECRET --project src/LineCompanionBot` を実行してください。
