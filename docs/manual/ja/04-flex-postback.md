[← 第3章](03-pet-growth-engine.md) | [索引](README.md) | [第5章 →](05-rich-menu.md)

# 第4章 — Flex Message応答とpostback分岐

**このステップで作るもの:** ステータスカードを描画する `PetFlexMessageFactory` と、Webhookハンドラの
オウム返し分岐を実際の相棒の世話分岐へ置き換える作業です。これで、`"action=feed"` / `"action=play"` /
`"action=status"` を運ぶ `PostbackEvent` が `PetGrowthEngine` を駆動し、その結果をFlex Messageで返す
流れができあがります。

## Flex Messageを手組みする

`FlexBubble` / `FlexBox` / `FlexText` は、素の生成POCOにすぎません。次章のリッチメニュー向け
`RichMenuClient` とは違って、`Line.OpenApi.Messaging` にはそれらを組み立てるファサードが用意されて
いないのです。そこで、その形を手組みする役目を `PetFlexMessageFactory` 一箇所に集約します。
`src/LineCompanionBot/Services/PetFlexMessageFactory.cs` を作成します:

```csharp
using Line.OpenApi.Messaging.Generated.Api.Models;

namespace LineCompanionBot.Services;

public static class PetFlexMessageFactory
{
    public static FlexMessage BuildStatus(PetState state)
    {
        var level = PetGrowthEngine.Level(state);
        var stage = PetGrowthEngine.Stage(state);

        var body = new FlexBox
        {
            Type = "box",
            Layout = FlexBox_layout.Vertical,
            Contents = new List<FlexComponent>
            {
                new FlexText { Type = "text", Text = $"{StageEmoji(stage)} Lv.{level} ({stage})", Weight = FlexText_weight.Bold, Size = "lg" },
                new FlexText { Type = "text", Text = $"Hunger {Bar(state.Hunger)} {(int)state.Hunger}%", Size = "sm", Margin = "md" },
                new FlexText { Type = "text", Text = $"Happy  {Bar(state.Happiness)} {(int)state.Happiness}%", Size = "sm" },
            },
        };

        var header = new FlexBox
        {
            Type = "box",
            Layout = FlexBox_layout.Vertical,
            Contents = new List<FlexComponent> { new FlexText { Type = "text", Text = state.Name, Weight = FlexText_weight.Bold, Size = "xl" } },
        };

        return new FlexMessage
        {
            Type = "flex",
            AltText = $"{state.Name}: Lv.{level}, Hunger {(int)state.Hunger}%, Happy {(int)state.Happiness}%",
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

    private static string StageEmoji(PetStage stage) => stage switch
    {
        PetStage.Hatchling => "\U0001F95A", // たまご
        PetStage.Juvenile => "\U0001F423",  // かえりかけのひな
        PetStage.Adult => "\U0001F414",     // にわとり
        _ => "?",
    };

    private static string Bar(double percent)
    {
        var filled = Math.Clamp((int)Math.Round(percent / 10), 0, 10);
        return new string('█', filled) + new string('░', 10 - filled);
    }
}
```

`BuildPlayRefused` は、その失敗分岐と対になるカードで、`PetGrowthEngine.Play` が `Success: false` を
返したときに表示されます。このカードの背後には、2つの設計判断が隠れています:

- **ステータスはテキストの進捗バーで描画する**（`"█████░░░░░ 50%"`）。ペットの絵ではありません。
  Flexの画像は公開の到達可能なHTTPS URLを要求し、つまりLINEのサーバから届くどこかに画像アセットを
  ホスティングしなければならないからです。たった2本のステータスバーを描くために引き受けるには、
  割の合わない手間です。テキストならアセットホスティングは要らず、その場で描けます。
- **入力面は1つ。** このbubbleにはフッターボタンをあえて置いていません。相棒の世話は、すべてリッチメニュー
  ([第5章](05-rich-menu.md))経由で行うからです。ここでFlexボタンを足して重複させてしまうと、同じことを
  する方法が2つ生まれてしまいます。

> **落とし穴: 各ノードに `type` を設定する。** 上のコードで `FlexMessage`/`FlexBubble`/`FlexBox`/`FlexText`
> がそれぞれ `Type`（`"flex"`/`"bubble"`/`"box"`/`"text"`）を設定している点に注目してください。これらは
> 素の生成 POCO で `type` 判別子にデフォルト値が無く、設定されたときだけシリアライズされます。省くと
> LINE が返信ボディを `400` で弾きます。オブジェクトの生成自体はオフラインでも成功するので見落としやすく、
> 実際に送信する[第9章](09-end-to-end.md)で初めて表面化します。

## オウム返しをpostback分岐に置き換える

それでは `Endpoints/WebhookEndpoints.cs` のイベントループを書き換えていきましょう。第2章の
`foreach (var ev in ...) { ... }` ブロック**全体**を、下記の `foreach` に丸ごと置き換えます。あわせて
ハンドラの引数に `IPetStore petStore` を、`CancellationToken ct` の直前に加えてください（こちらは
無条件に登録されているので `[FromServices]` は要りません。ゲートされた `parser`/`messaging` とは
対照的です）。新しいコードは `PetGrowthEngine`/`PetFlexMessageFactory`（`LineCompanionBot.Services`）と
`IPetStore`（`LineCompanionBot.Persistence`）を参照するので、ファイル冒頭に
`using LineCompanionBot.Services;` と `using LineCompanionBot.Persistence;` を追加します:

```csharp
foreach (var ev in callback.Events ?? new())
{
    if (ev is not PostbackEvent { ReplyToken: { Length: > 0 } replyToken } postback || messaging is null)
        continue;
    // このペットはユーザー単位。group/room ソースは UserId を持たないのでスキップする。
    if (postback.Source is not UserSource { UserId: { Length: > 0 } userId })
        continue;

    var now = DateTimeOffset.UtcNow;
    var pet = await petStore.GetOrCreateAsync(userId, now, ct);

    FlexMessage reply;
    switch (postback.Postback?.Data)
    {
        case "action=feed":
            pet = PetGrowthEngine.Feed(pet, now);   // 第6章で、この分岐を Golden Kibble を消費するよう拡張する
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
            continue; // 認識できない postback データ
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

ユーザーを解決し、分岐し、返信する。これでハンドラの全貌が出そろいました。返信呼び出しは、第2章と同じく
try/catch で包んであります。失敗してもログに残すだけで、エンドポイントはそれでも200を返します。
ここでも、期限切れのリプライトークンがリトライを誘発するのは避けたいからです。なお `CancellationToken ct`
は `HttpContext.RequestAborted` から渡ってきて（自動でバインドされるので属性は不要です）、ストア呼び出しと
返信呼び出しの両方へ流れます。

> `"action=..."` という文字列は、適当に決めたものではありません。[第5章](05-rich-menu.md)のリッチメニューが
> まさに送るpostbackデータそのものです。メニューが話しかける相手を先に用意しておくために、分岐を先に
> 組み込んでいます。

## 試してみる — postbackをシミュレートする

イベントループは `messaging` が `null` のときは全イベントをスキップします（`|| messaging is null` の
ガード）。`MessagingClient` は `LINE_CHANNEL_ACCESS_TOKEN` が設定されているときだけ登録されるので、
第2章のシークレットだけを入れた状態では、ハンドラは `switch` に到達する前に `continue` してしまい、何も
分岐されません。そこで、クライアントが登録されて分岐が実際に動くよう、ダミーのアクセストークンも設定して
おきます（`api.line.me` への返信は、トークンが偽物なのでやはり失敗しますが、それこそがここで観察
したいことです）:

```powershell
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "demo-token" --project src/LineCompanionBot
```

（第2章で入れた）`LINE_CHANNEL_SECRET` をuser-secretsに残したまま、今度は `message` ではなく `postback`
イベントを運ぶペイロードを、自分の手で署名してみましょう:

```powershell
$body = '{"destination":"xxx","events":[{"type":"postback","replyToken":"dummy","source":{"type":"user","userId":"U123"},"postback":{"data":"action=feed"},"timestamp":1,"mode":"active"}]}'
# ...第2章とまったく同じ方法で署名して POST する...
```

`switch` にブレークポイントを置いて、F5でリクエストをデバッグしてみてください。`U123` を解決し、`Feed` を
実行し、`FlexMessage` を組み立てる一部始終が追えるはずです。実チャネルアクセストークンが無ければ、
`api.line.me` への返信呼び出しは失敗してログに残りますが、それでもエンドポイントは200を返します。
第2章と同じ扱い（失敗はログに残して200を返すだけ）が、今度は実際にLINEのAPIを呼び出すところまで
広がった形です。カードが実際に手元まで届くよう実トークンを設定する手順は、[第9章](09-end-to-end.md)で
扱います。
