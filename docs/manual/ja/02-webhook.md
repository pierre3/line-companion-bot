[← 第1章](01-project-skeleton.md) | [索引](README.md) | [第3章 →](03-pet-growth-engine.md)

# 第2章 — Webhook受信 + 署名検証

**このステップで作るもの:** `POST /webhook` です。生のリクエストボディに対するLINEのHMAC-SHA256署名を
検証し、ペイロードをパースして、（今はまだ）テキストメッセージをそのままオウム返しします。このオウム
返し分岐は、[第4章](04-flex-postback.md)で実際の相棒の世話分岐へ置き換えていきます。なぜ最初から本番の
ロジックを書き下ろさないのか——動作確認済みのオウム返しから始めておけば、あとは一度に一つのことだけを
デバッグすればよくなるからです。

**どこに置くか:** 最初から専用ファイル——`Endpoints/WebhookEndpoints.cs`——に置き、`MapWebhookEndpoint()`
拡張メソッドとして公開します。というのも、Minimal APIでは、ハンドラが些細でなくなってきたら `Program.cs`
から本格的なハンドラを切り出すことが推奨されているからです。このアプリも最終的にはそうしたハンドラを
2つ抱えることになります（ここでのwebhookと、[第6章](06-shop.md)のshop）。だとすれば、今のうちから
最終的な置き場所で組み立てておくほうが、後で移動する手間を避けられて素直です。

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

`Program.cs` のヘルスエンドポイントの後に配線します:

```csharp
app.MapWebhookEndpoint();
```

`MapWebhookEndpoint` は `LineCompanionBot.Endpoints` にある拡張メソッドなので、`Program.cs` の冒頭に
`using LineCompanionBot.Endpoints;` を追加してください——第1章の縮約された `using` ブロックでは、
まだこれを参照していませんでした。

ここで押さえておきたい、そしてこのアプリ全体で繰り返し顔を出すことになるポイントが3つあります:

- **`parser` と `messaging` の `[FromServices]` は飾りではなく必須です。** 理由はシンプルで、どちらも
  *条件付きで*登録されるからです（第1章の `HasWebhook` / `HasMessaging` ゲート）。ASP.NET Coreの
  「これはDIサービスか、それともボディ/ルート値か?」という自動推論は、起動時に登録されているのが
  見える型しか認識してくれません——条件付きで登録される型は「ボディ」だろうと推測され、そのままでは
  ルートの構築自体に失敗してしまいます。そこでこの属性でDI解釈を明示的に強制するわけです。（対照的に、
  後の章で登場する*無条件に*登録されるサービスでは、この属性は正しく省略されています。）
- **何よりも先に生のバイト列を読む。** というのも、HMACは正確なリクエストボディそのものに対して
  計算されるからです。フレームワークに先にモデルバインディングをさせてしまうと、ストリームが消費され、
  手元に残るバイト列が変わってしまいます。
- **返信が失敗してもログに残すだけで、200は返す。** 返信はときに失敗します——いちばん多いのはリプライ
  トークンの期限切れ（有効期間は約1分）でしょう。とはいえLINEは非2xx応答をリトライするので、返信失敗を
  そのまま非2xxに変えてしまうと、今度は重複配信の嵐を招いてしまいます。だからここは例外を捕まえてログに
  残し、それでも200を返します。

## 試してみる — LINEチャネル不要

実は、LINEと全く同じ方式でペイロードを自分の手で署名できます。まずはwebhook登録が有効になるよう、
使い捨てのシークレットをuser-secretsに設定してから、F5で起動しましょう:

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

正しい署名は受理され、改ざんされた署名は拒否される——このどちらも、LINEチャネル抜きに手元だけで
確かめられます。ハンドラにブレークポイントを置いて再送してみて、デバッガの下でパースが成功していく
様子を眺めてみてください。実チャネルをdev tunnel経由で繋ぐ手順のほうは、[第9章](09-end-to-end.md)で
扱います。

> **ヒント:** `GET /` が `webhook: enabled` と報告するようになっているはずです。未設定の状態に戻したく
> なったら、`dotnet user-secrets remove LINE_CHANNEL_SECRET --project src/LineCompanionBot` を実行してください。
