[← 第3章](03-pet-growth-engine.md) | [索引](README.md) | [第5章 →](05-rich-menu.md)

# 第4章 — Flex Message応答とpostback分岐

**このステップで作るもの:** ステータスカードを描画する `PetFlexMessageFactory`、そしてWebhookハンドラの
オウム返し分岐を、実際の相棒の世話分岐へと置き換える作業です。これで、`"action=feed"` / `"action=play"` /
`"action=status"` を運ぶ `PostbackEvent` が `PetGrowthEngine` を駆動し、その結果をFlex Messageで返す——
という流れができあがります。

## Flex Messageを手組みする

`FlexBubble` / `FlexBox` / `FlexText` は、素の生成POCOにすぎません——次章のリッチメニュー向け
`RichMenuClient` とは違って、`Line.OpenApi.Messaging` にはそれらを組み立ててくれるファサードが用意されて
いないのです。そのため、その形を手組みする役目は `PetFlexMessageFactory` 一箇所に集約することにします。
`src/LineCompanionBot/Services/PetFlexMessageFactory.cs` を作成します:

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

`BuildPlayRefused` は、その失敗分岐と対になるカードで、`PetGrowthEngine.Play` が `Success: false` を
返したときに表示されます。このカードの背後には、2つの設計判断が隠れています:

- **ステータスはテキストの進捗バーで描画する**（`"█████░░░░░ 50%"`）——ペットの絵ではありません。
  というのも、Flexの画像は公開の到達可能なHTTPS URLを要求し、それはつまり、LINEのサーバから届くどこかに
  画像アセットをホスティングしなければならない、ということだからです——たった2本のステータスバーを描く
  ために解決するには、あまりに割の合わない本物の問題です。テキストであれば、アセットホスティングなど一切
  要らず、その場で即座に描けます。
- **入力面は1つ。** このbubbleにはフッターボタンをあえて置いていません。相棒の世話は、すべてリッチメニュー
  ([第5章](05-rich-menu.md))経由で行うからです。ここでFlexボタンを足して重複させてしまうと、同じことを
  する方法が2つ生まれてしまいます。

## オウム返しをpostback分岐に置き換える

それでは `Endpoints/WebhookEndpoints.cs` のイベントループを書き換えていきましょう。ハンドラの引数に
`IPetStore petStore` を加え（こちらは無条件に登録されているので `[FromServices]` は要りません——ゲート
された `parser`/`messaging` とはちょうど対照的です）、`MessageEvent` のオウム返し分岐を置き換えます:

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

ユーザーを解決し、分岐し、返信する——これでハンドラの全貌が出そろいました。返信呼び出しを包んでいるのは、
第2章とまったく同じ「吸収してack」のイディオムです。ここでも、期限切れのリプライトークンがリトライの嵐を
招くようなことは避けたいですからね。なお `CancellationToken ct` は `HttpContext.RequestAborted` から流れ
込んできて（自動でバインドされるので、属性は不要です）、ストア呼び出しと返信呼び出しの両方へ渡っていきます。

> `"action=..."` という文字列は、適当に決めたものではありません——[第5章](05-rich-menu.md)のリッチメニューが
> まさに送るように作られている、そのpostbackデータそのものです。メニューが話しかける相手を先に用意しておく
> ために、分岐のほうをここで先に配線しているわけです。

## 試してみる — postbackをシミュレートする

（第2章で入れた）`LINE_CHANNEL_SECRET` をuser-secretsに残したまま、今度は `message` ではなく `postback`
イベントを運ぶペイロードを、自分の手で署名してみましょう:

```powershell
$body = '{"destination":"xxx","events":[{"type":"postback","replyToken":"dummy","source":{"type":"user","userId":"U123"},"postback":{"data":"action=feed"},"timestamp":1,"mode":"active"}]}'
# ...sign and POST exactly as in Chapter 2...
```

`switch` にブレークポイントを置いて、F5でリクエストをデバッグしてみてください。`U123` を解決し、`Feed` を
実行し、`FlexMessage` を組み立てていく——その一部始終が追えるはずです。実チャネルアクセストークンが無ければ、
`api.line.me` への返信呼び出しは失敗してログに残りますが——それでもエンドポイントはちゃんと200を返します。
第2章と同じ「吸収してack」のイディオムが、今度は実際のダウンストリーム呼び出しまでカバーしているわけです。
カードが実際に手元まで届くよう実トークンを配線する手順は、[第9章](09-end-to-end.md)で扱います。
