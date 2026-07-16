# チュートリアル: バーチャル相棒育成Bot × MINI Appショップを作る

これは `LineCompanionBot` がどう組み立てられたかを、実装ステップに沿って追体験できるハンズオン
記事です。アプリ本体と並行して書かれています——各章は対応する実装ステップが完了した時点で
書かれているので、ここに書いてあるのは後から振り返った要約ではなく、実際に起きたことです。

各 `Line.OpenApi.*` パッケージのAPI仕様を再説明することはしません（それは
[`line-dotnet` の概念記事](https://github.com/pierre3/line-openapi-dotnet)を参照してください）。
ここで見せたいのは**複数パッケージをどう繋ぐか**——LINEチャットでバーチャル相棒を育てるBotと、
IAP（アプリ内課金）でアイテムを買えるMINI Appショップを1つのシステムにする方法です。

## 第1章 — プロジェクトの骨組みとDI配線

**このステップで作るもの:** 起動して自分の設定状態を報告するだけの、最小のアプリ。以降の章は
すべてこの土台の上に積み上がっていきます。

**なぜここから始めるか:** `line-dotnet` の `Line.OpenApi.*` サンプルアプリはどれも同じ形——
`appsettings.json`バインドを使わない素の環境変数設定で、何も設定していなくてもアプリは
**必ず起動し**、起動を拒否する代わりにヘルスエンドポイントで「何が足りないか」を報告する、
という規約です。これを最初に確立しておけば、以降の章はこの前提の上に安心して積み増せます。

**コード:**

`CompanionSettings.cs` は、アプリが必要とする設定を起動時に一度だけ環境変数から読み込みます:

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
    // FromEnvironment() が LINE_CHANNEL_SECRET / LINE_CHANNEL_ACCESS_TOKEN /
    // LINE_MINIAPP_LIFF_ID / LINE_MINIAPP_TEMPLATE_NAME / LINE_MINIAPP_POLL_SECONDS を読む。
}
```

`Program.cs` は使う3パッケージを、それぞれ必要な設定が揃っている時だけ配線します:

```csharp
if (settings.HasWebhook)
    builder.Services.AddLineWebhook(o => o.ChannelSecret = settings.ChannelSecret!);

if (settings.HasMessaging)
    builder.Services.AddLineMessaging(o => o.ChannelAccessToken = settings.ChannelAccessToken!);

// MiniAppClientはDIオプションではなく呼び出し毎の引数でトークンを受け取るので、設定は不要。
builder.Services.AddLineMiniApp();
```

`AddLineMiniApp()` には必須設定が無い点に注目してください——`AddLineWebhook`/
`AddLineMessaging`と違い、`MiniAppClient`の各メソッドはチャネル/ユーザーアクセストークンを
すべて呼び出し毎の引数として受け取る設計（`MiniAppClient`のXMLドキュメント参照）なので、
そもそもゲートする対象の設定が存在しません。

**動かしてみる:**

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

設定ゼロでもアプリは起動し、次に何を設定すればよいかを正確に教えてくれます——これが以降の章で
機能を追加していく際の土台になるパターンです。

## 第2章 — Webhook受信 + オウム返し応答

**このステップで作るもの:** `POST /webhook`。`line-dotnet`の`Line.OpenApi.Samples.Webhook`サンプル
と全く同じ配線——署名検証→本文デシリアライズ→今はまだテキストメッセージをオウム返しするだけ。
以降の章でこのオウム返し部分を相棒の世話postback分岐に置き換えます——まず動作確認済みの土台を
作ってから拡張する、という順番です。

**コード** (`Program.cs`):

```csharp
app.MapPost("/webhook", async (
    HttpRequest request,
    [FromServices] WebhookRequestParser? parser,
    [FromServices] MessagingClient? messaging) =>
{
    if (parser is null)
        return Results.Problem("LINE_CHANNEL_SECRET is not configured.", statusCode: 503);

    // 署名は生のバイト列に対して計算されるため、モデルバインディングより先に読む。
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

    // 常に素早く200を返す: LINEは非2xx応答をリトライするため、応答遅延や失敗は重複配信を招く。
    return Results.Ok();
});
```

「ダウンストリーム障害を吸収し、常に200をack」というイディオムがここで重要なのは、LINEの
webhook配信が非2xx応答をリトライするからです——返信失敗（例: 約1分で失効するリプライトークンの
期限切れ）が重複配信の嵐に発展してはいけません。

**動かしてみる**（まだ実チャネル不要——LINEと同じ方式で自己署名する）:

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

正しい署名は受理され、改ざんされた署名は拒否されることをローカルで確認してから次に進みます。
実チャネルをdev tunnel経由で繋ぐ手順はエンドツーエンドの章で扱います。

## 第3章 — Pet状態と成長エンジン

**このステップで作るもの:** ペットのシミュレーション本体——`PetState`・`PetGrowthEngine`・
`PetStore`——LINE APIには一切依存しません。ここは意図的にこのアプリで唯一ユニットテストを
書く部分です: `line-dotnet`の他のサンプルはすべてテスト無しですが、ここは減衰クランプ・
レベル閾値・空腹ゲートという実際のエッジケースを持つ純粋な分岐ロジックなので、単体で検証する
コストが低く価値が高いためです。

**設計判断とその理由:**

- **遅延減衰、バックグラウンドタイマーなし。** `PetGrowthEngine.ApplyDecay`は、ペットに触れる
  たび（餌やり・遊ぶ・様子を見る）に経過した実時間からHunger/Happinessの減少を計算します。
  数秒ごとにtickして減衰をシミュレートする`BackgroundService`は、シミュレーションのための
  シミュレーションになってしまいます——そもそも操作の合間にペットを観測している人はいません。
- **「死亡」メカニクスなし。** `Hunger<=20`の状態で`play`を試みると失敗します——
  `Success=false`の`PlayResult`——が、何も失われません。デモがチェックインしなかったユーザーを
  恒久的に罰することは絶対にしない設計です。失敗分岐はエラー/分岐処理の見せ場として存在する
  のであって、ゲームを厳しくするためではありません。
- **レベルはテーブルではなく数式から。** `Level = 1 + Xp/50`——ただの整数除算。ルックアップ
  テーブルも曲線もありません。3つの進化段階（`Hatchling`/`Juvenile`/`Adult`）はレベルから
  そのまま区分されます。

**コード** (`Services/PetGrowthEngine.cs`):

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

`PetStore`は`ConcurrentDictionary<string, PetState>`をシングルトンでラップしただけ——
`IPetStore`インターフェースは作りません、実装が1つ・呼び出し元が1箇所しかないためです。
このアプリの他のインメモリストアと同じ方針: 永続化なし、再起動で状態はリセットされる、
デモとしてはそれで十分です。

**動かしてみる:**

```powershell
dotnet test
```

```
成功!   -失敗:     0、合格:    16、スキップ:     0、合計:    16
```

減衰の0クランプ、feed/playの100クランプ、空腹ゲートによる`play`拒否、レベル→進化段階の境界を
16件のテストで網羅しています。まだLINEとは何も通信していません——それは次の章です。

## 第4章 — Flex Message応答と、オウム返しをpostback分岐へ置き換え

**このステップで作るもの:** `PetFlexMessageFactory`と、Webhookハンドラのオウム返し部分を実際の
相棒の世話分岐に置き換える作業——`Data`が`"action=feed"`/`"action=play"`/`"action=status"`の
`PostbackEvent`が`PetGrowthEngine`を駆動し、Flex Messageのステータスカードで応答します。

**なぜFlex Messageを手組みするか:** `FlexBubble`/`FlexBox`/`FlexText`は素の生成POCOです——
`Line.OpenApi.Messaging`にはそれらを組み立てるファサードがありません（リッチメニュー向けの
`RichMenuClient`のようなものとは違います）。そのため`PetFlexMessageFactory`が、このアプリで
唯一その形を手組みする場所になります。

**設計判断 — 画像ではなくテキストのプログレスバー。** ステータスカードはHunger/Happinessを
ペットの絵ではなく`"█████░░░░░ 50%"`というテキストで表現します。Flexの画像はLINEのサーバから
到達可能な公開HTTPS URLが必要で、それはデモアプリのために解決する価値のない、画像アセットの
ホスティングという本物の問題を生みます。テキストなら即座に描画でき、アセットホスティングも
不要です。

**設計判断 — 入力導線は1本化。** 応答の`FlexBubble`には`Footer`もquick replyボタンもありません。
相棒の世話はすべて（次章で作る）リッチメニュー経由——Flexボタンで重複させると、同じことをする
方法が2つできてしまうだけです。

**コード** (`Services/PetFlexMessageFactory.cs`):

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
        Contents = new FlexBubble { Header = /* 名前ヘッダー */, Body = body },
    };
}
```

`BuildPlayRefused`は`PetGrowthEngine.Play`が`Success=false`を返した時に表示される、失敗分岐の
対になるものです。

Webhookハンドラ (`Program.cs`) は、イベントの`Source`（`UserSource`が`UserId`を持つ——グループ/
ルームなど他のsource型はこのペットがユーザー単位のため無視）からLINEユーザーIDを解決し、
postbackの`Data`文字列で分岐します:

```csharp
if (ev is not PostbackEvent { ReplyToken: { Length: > 0 } replyToken } postback || messaging is null)
    continue;
if (postback.Source is not UserSource { UserId: { Length: > 0 } userId })
    continue;

var pet = petStore.GetOrCreate(userId, now);
FlexMessage reply = postback.Postback?.Data switch
{
    "action=feed" => /* PetGrowthEngine.Feed + BuildStatus */,
    "action=play" => /* PetGrowthEngine.Play + BuildStatus または BuildPlayRefused */,
    "action=status" => /* PetGrowthEngine.Status + BuildStatus */,
    _ => /* 未知のpostbackデータ: スキップ */,
};
```

**動かしてみる:** まだリッチメニューは無い（次章）ので、postbackイベントは直接シミュレート
できます——`message`イベントの代わりに`postback`イベントを自己署名してPOSTします:

```powershell
$body = '{"destination":"xxx","events":[{"type":"postback","replyToken":"dummy","source":{"type":"user","userId":"U123"},"postback":{"data":"action=feed"},"timestamp":1,"mode":"active"}]}'
# ...第2章と同様に署名してPOST...
```

実チャネルアクセストークンが設定されていれば、これは`api.line.me`への実際の返信を試みます。
プレースホルダートークン（または本サンドボックスのようにネットワークアクセスが遮断された
開発環境）では返信呼び出しは失敗してログに記録されますが、エンドポイントは`200`を返し続けます
——第2章と同じ「吸収してack」のイディオムが、今度は実際のダウンストリーム呼び出しに対しても
機能していることになります。ローカルでは、例外を投げずにLINEの返信エンドポイントを呼び出す
ところまで到達することを確認済みです。実トークンとリッチメニューを組み合わせた確認は
エンドツーエンドの章で行います。

## 第5章 — リッチメニューのブートストラップ (`dotnet run -- setup`)

**このステップで作るもの:** リッチメニューを作成し、画像をアップロードし、アカウントの
デフォルトに設定する使い捨てCLIコマンド——第4章のpostbackデータ文字列（`"action=feed"`等）を、
ユーザーが実際にタップできるものに変える最後のピースです。

**なぜHTTPエンドポイントではなくCLIコマンドか。** デフォルトのリッチメニュー設定はアカウント
全体に影響します——チャネルの全ユーザーに及びます。これは管理操作であり、このアプリはWebhook
のためにdev tunnel経由でインターネットに公開されています。`POST /setup`エンドポイントを作ると、
破壊的で未認証の管理操作をWebhookと同じ公開面に置くことになります。`args[0]=="setup"`で
`WebApplication`が構築される**前**に分岐させることで、ローカル限定の操作に留めます——
`Line.OpenApi.Samples.Console`（`dotnet run -- send`、`dotnet run -- webhook`等）で既に使われている
verbスタイルに合わせています。

**ブロッキングな前提条件: 実際の画像ファイル。** `RichMenuClient.SetImageFromFileAsync`は
ディスク上の実際のPNGを必要とします——実ピクセルのアップロードを回避する方法はありません。
このリポジトリには画像生成ライブラリが存在せず（4つの色付き四角を描くためだけに追加するのは
不釣り合いな新規依存になります）、そこで`assets/richmenu.png`のプレースホルダーは、アプリの
一部ではない使い捨てのPowerShell + `System.Drawing`スクリプトで、一度だけout-of-bandで
生成しました（ビルド成果物ではなく、コミットする静的アセットです）:

```powershell
Add-Type -AssemblyName System.Drawing
# ...2500x1686のキャンバスに1250x843の4象限（FEED/PLAY/STATUS/SHOP）をラベル付きで描画...
$bmp.Save("assets/richmenu.png", [System.Drawing.Imaging.ImageFormat]::Png)
```

デモの範囲を超えて使う場合は、このファイルを実際のアートワークに差し替えてください。

**コード** (`Services/RichMenuBootstrapper.cs`):

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
        AreaUri(HalfWidth, HalfHeight, shopUri), // 第6章のMINI AppショップLIFF URL
    },
};

var richMenu = RichMenuClient.CreateWithStaticToken(settings.ChannelAccessToken!);
var richMenuId = await richMenu.CreateAsync(request);
await richMenu.SetImageFromFileAsync(richMenuId!, imagePath);
await richMenu.SetDefaultAsync(richMenuId!);
```

4つのエリアのうち3つは`PostbackAction`（Webhookハンドラが既に分岐している`"action=feed"`/
`"action=play"`/`"action=status"`文字列と対応）、残る1つはMINI AppショップのLIFF URLを指す
`URIAction`です——ショップボタンにpostbackは不要で、LINEがそのままURLを開きます。

**動かしてみる**（CLI配線自体の確認に実チャネルは不要）:

```powershell
dotnet run -- setup
```

```
LINE_CHANNEL_ACCESS_TOKEN is not set — cannot create a rich menu.
```

Webホストが起動する前にこのverbが分岐し、未設定時はクラッシュせずサーバも起動せずに明確な
メッセージで終了することを確認しました。実チャネルアクセストークンに対して実行する
（エンドツーエンドの章で扱う）と、実際にメニューが作成・有効化されます。

## 第6章 — MINI Appショップ: フロントエンドとバックエンド

**このステップで作るもの:** リッチメニューのショップボタンが開く先——`wwwroot/shop`から配信
される素のHTML/JSページと、それを支える3つのエンドポイント（`/api/shop/config`・
`/api/shop/catalog`・`/api/shop/reserve`）＋在庫確認。

**リクエスト契約と、各フィールドの出所。** `ReserveProductAsync`にはユーザーアクセストークン・
クライアントのIP・OSが必要ですが、その呼び出し連鎖のどこからもLINEユーザーIDは得られません。
バックエンドが自力で導出できないものは、フロントエンドが供給します:

```js
const profile = await liff.getProfile();       // -> userId
const token = liff.getAccessToken();           // -> ReserveProductAsyncが必要とするユーザーアクセストークン
const os = liff.getOS();                       // -> "ios" | "android"（UA判定より確実）
```

これらと`productId`が`/api/shop/reserve`のリクエストボディになります。`clientIp`はサーバ側で
`X-Forwarded-For`（dev tunnel越しに存在）から埋め、無ければ`HttpContext.Connection.RemoteIpAddress`
にフォールバックします。

**バックエンド** (`Program.cs`):

```csharp
app.MapPost("/api/shop/reserve", async (ShopReserveRequest req, MiniAppClient miniApp, ...) =>
{
    var item = ShopCatalog.Find(req.ProductId);
    if (item is null) return Results.NotFound(...);

    // ベストエフォート — ここでの失敗が購入自体を止めない理由は第8章参照。
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

2点、特筆すべき点があります:

- **notifierトークンの発行は購入完了時ではなくここで行う。** `IssueNotificationTokenAsync`は
  フロントエンドしか持っていないLIFFアクセストークンを必要とします——ユーザーがショップを
  操作しているまさにその瞬間です。トークンは今ここで保存され、後で（このリクエストが返ってから
  数秒〜数十秒後に）購入が実際に完了した時点で`PurchaseReconciliationService`（第7章）が使います。
- **notifier呼び出しはreserve呼び出しとは別のtry/catchで包む。** notifierトークンの発行が
  失敗しても（第8章のトークン種別に関する注意点参照——十分にあり得ます）、購入自体は進行しな
  ければなりません。リクエストを失敗させてよいのは`ReserveProductAsync`の失敗だけです。

**意図的に埋めていないブロッキングTODO。** `reserve`が`orderId`を返した後、フロントエンドは
それをLINEのアプリ内課金JS SDKに渡して実際の購入UIを駆動するはずです。そのSDKは
`Line.OpenApi.*`がラップするものではありません——クライアント側のMINI App固有JavaScriptで
あり、そのメソッド名を推測することは、明確にマークされた欠落を残すことよりも悪い選択です:

```js
// TODO: `orderId`をLINEのアプリ内課金SDKに渡してトランザクションを完了させる。
// 正確な呼び出しはLine.OpenApi.*のスコープ外——実装時にLINE公式のMINI App IAPドキュメントで
// 確認すること。推測しないこと。
```

**動かしてみる**（バックエンド契約の検証にMINI Appチャンネルはまだ不要）:

```powershell
$env:LINE_MINIAPP_LIFF_ID = "1234567890-abcdefgh"   # プレースホルダーで可
dotnet run
```

```powershell
Invoke-RestMethod http://localhost:5091/api/shop/catalog
# -> 3点のカタログ（Golden Kibble / Party Hat / Star Badge）

Invoke-RestMethod http://localhost:5091/api/shop/reserve -Method Post -ContentType 'application/json' -Body '{}'
# -> 400（必須フィールド欠落）

Invoke-RestMethod http://localhost:5091/api/shop/reserve -Method Post -ContentType 'application/json' `
    -Body '{"userId":"U1","productId":"unknown-item","liffAccessToken":"fake"}'
# -> 404（未知のproductId）
```

catalog・config・inventoryの各エンドポイントが正しく応答すること、reserveエンドポイントの
検証分岐（フィールド欠落→400、未知の商品→404）がネットワーク呼び出しより前に発火することを
確認しました。実MINI Appチャンネルと実LIFFアクセストークンがあれば、同じリクエストは実際に
`ReserveProductAsync`を呼び出すところまで進みます——それはエンドツーエンドの章で扱います。

## 第7章 — 購入照合

**このステップで作るもの:** `PurchaseReconciliationService`——完了した購入を検出し、対応する
アイテムを付与する`BackgroundService`。第6章の`reserve`呼び出しで開いたループを閉じるピースです。

**なぜポーリングか、そしてなぜこのシステム全体で唯一本当に不格好な部分なのか。**
`MiniAppClient`にはIAPイベント向けのpush webhookがありません（第2章のMessaging webhookとは
違います）——`GetWebhookEventsAsync`は7日間の窓をカーソルページングで辿るpull APIです。
即座に反応する代わりに、このサービスはタイマー（`LINE_MINIAPP_POLL_SECONDS`、既定30秒）で
tickし「前回確認してから何が起きたか」を問い合わせます。

**ポーリングループ** (`Services/PurchaseReconciliationService.cs`):

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    if (!_settings.HasMessaging) { /* ログを出してreturn — トークンが無ければポーリングしようがない */ }

    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.PollSeconds));
    do
    {
        try { await PollOnceAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogWarning(ex, "Purchase reconciliation poll failed; will retry next tick."); }
    } while (await timer.WaitForNextTickAsync(stoppingToken));
}
```

ポーリング失敗は握りつぶして次のtickでリトライします——Webhookハンドラと同じ「吸収して続行」
イディオムを、HTTP応答ではなくバックグラウンドループに適用したものです。

**各ポーリングは、watermarkを進める前に窓内の全ページを走査します:**

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

    _watermarkEpochSeconds = now; // 窓内の全ページが成功した後にのみ前進
}
```

特筆すべき設計判断が3点あります:

- **`OrderStore`が認識するオーダーのみを処理する。** `MiniAppWebhookEvent`は`UserId`と
  `ProductId`を直接持っているため、ユーザーを*解決する*ためだけなら`OrderStore`は厳密には
  不要です——それでもゲートとして参照するのは、このアプリが自分で`reserve`した購入だけに
  アイテムを付与し、同じチャネル上に存在するかもしれない他のIAP活動には反応しないためです。
- **「処理済み」を別途追跡するのではなく、構造的に冪等。** `InventoryStore.Grant`は`OrderId`を
  キーにして、繰り返しには単に何もしません——アプリが窓の途中で再起動し、重複した範囲を
  再走査しても二重付与にはなりません。別途「処理済みイベント」集合は不要です。
- **watermarkは全ページが成功した後にのみ前進する。** イベント単位で前進させると、ページの
  途中でループが中断された場合に静かな抜け漏れを招くリスクがあります。

**払い戻しは対称的に処理します**——`refundComplete`は同じ`OrderId`でアイテムを取り消します。
コード量はわずかですが、レスポンス形状にそのフィールドが存在することを見せる価値があります。

**動かしてみる:** チャネルアクセストークンが無ければ、サービスは無効である旨をログに出して
何もしません——どちらの場合もアプリが健全なままであることを確認しました:

```
info: LineCompanionBot.Services.PurchaseReconciliationService[0]
      LINE_CHANNEL_ACCESS_TOKEN is not set — purchase reconciliation is disabled.
```

トークンを設定すると、このサービスは第2章・第4章で既に見たのと同じネットワーク境界に到達します
——このサンドボックス化された開発環境では`api.line.me`への外向き呼び出しが遮断されているため、
ポーリング試行は失敗してログに記録され、設計通り次のtickでリトライされます。実際に本物の
`purchaseComplete`イベントを検出できることの確認には、実MINI Appチャンネルと実際の購入完了が
必要です——それはエンドツーエンドの章で扱います。付与が起きた時に発火する通知は次で配線します。

## 第8章 — ユーザーへの通知: サービスメッセージ、Pushフォールバック付き

**このステップで作るもの:** `NotifyPurchaseAsync`——第7章のポーリングループで
`InventoryStore.Grant`が成功した直後に呼ばれる、ユーザーにチャットでアイテム獲得を実際に
伝えるステップです。

**先に触れておくべきDIの機微。** `MessagingClient`は`LINE_CHANNEL_ACCESS_TOKEN`が設定されている
時だけコンテナに登録されます（第1章のゲート）。`PurchaseReconciliationService`も*同じ*トークンが
設定されている時だけ実行されますが——`BackgroundService`のコンストラクタ依存は、`ExecuteAsync`が
後で何を判断するかに関わらず、ホストが起動時に即座に解決します。`MessagingClient`を直接
コンストラクタ引数として受け取ると、トークン未設定時にホストが起動時にクラッシュしてしまいます
——`ExecuteAsync`自体はどうせ即returnするだけなのに。解決策: 代わりに`IServiceProvider`
（フレームワークが提供する、常に解決可能なもの）を受け取り、既にトークンでゲートされた
`ExecuteAsync`の*内側で*`MessagingClient`を遅延解決します:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    if (!_settings.HasMessaging) { /* ログを出してreturn */ }

    var messaging = _serviceProvider.GetRequiredService<MessagingClient>(); // ここではトークンが設定済みなので安全
    // ...PollOnceAsync(messaging, ...) で NotifyPurchaseAsync まで受け渡す
}
```

これは、第2章で見たASP.NET Coreミニマル API の規約（エンドポイント引数の`[FromServices] MessagingClient?`）
がホストサービスで壁にぶつかる例です: ミニマル APIの引数バインディングはリクエスト時に解決する
ため未登録のオプショナルサービスを許容しますが、`ActivatorUtilities`によるコンストラクタ
インジェクションはそうではなく——すべてを事前に解決しようとします。`LINE_CHANNEL_ACCESS_TOKEN`
にプレースホルダートークンを設定して実行し、ホストが正常に起動しヘルスエンドポイントが即座に
応答すること（解決不能なコンストラクタ依存でホストがクラッシュしないこと）を確認済みです。

**通知ロジック** (`Services/PurchaseReconciliationService.cs`):

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

**なぜゲートが1条件ではなく2条件なのか。** 「テンプレートが設定されているか」だけでゲートしたく
なりますが、それだけでは*この特定ユーザー*向けに*使えるトークン*が存在する保証にはなりません:
`IssueNotificationTokenAsync`（第6章）は、そのユーザーが実際にLIFFアクセストークンを保持した
状態でショップを開いた場合にしか実行されておらず、トークンは5回送信すると使い切られます。
そのため条件は「`TemplateName`が設定されている **かつ** このユーザー向けの生きたトークンが
存在する」——どちらか半分でも欠けているか、サービスメッセージ呼び出しが何らかの理由で例外を
投げた場合（最も可能性が高いのは: このアプリの`LINE_CHANNEL_ACCESS_TOKEN`が長期トークンなのに
対し、notifierエンドポイントはstateless/short-livedトークンを要求する——`MiniAppClient`の
XMLドキュメントに明記された実制約です）、素の`Push`メッセージへフォールバックします。これは
既に設定済みのチャネルトークンだけで常に動作します。審査未実施の新規デモ環境では、**Push
フォールバックが通常経路であってエッジケースではありません**——それが設計意図です:
`SendServiceMessageAsync`を見せる価値は、2つの前提条件が揃った時に上乗せされる磨き上げに
あるのであって、デモが動くための必須要件ではありません。

**動かしてみる:** 第7章のログ出力で既に確認済みです——テンプレート未設定
（`LINE_MINIAPP_TEMPLATE_NAME`未設定が既定）であれば、どの付与もそのままPush分岐へ進みます。
実際の付与が発火するところを見るには本物の購入完了が必要です——それは次で扱います。

## 第9章 — 実チャネルでのエンドツーエンドと、トラブルシューティング

これまでの各章は、それぞれの部分をローカルで検証してきました——署名の往復、postback分岐、
Flex Message組み立て、CLIコマンド、ショップのHTTP契約、ポーリング＆リトライループ、いずれも
実LINEチャネル無しで確認済みです。この章で残っているのは、実チャネルを繋いで全部を一緒に
動かすことと、その際にありがちな詰まりどころです。

### Console設定、行き詰まらない順序で

1. [LINE Developers Console](https://developers.line.biz/console/)で**Messaging APIチャンネル**
   を作成。**チャネルシークレット**（→`LINE_CHANNEL_SECRET`）を控え、**チャネルアクセス
   トークン**（→`LINE_CHANNEL_ACCESS_TOKEN`）を発行。
2. 同じプロバイダー配下に**LINE MINI Appチャンネル**を作成。これは通常のLIFFアプリとは別の
   製品で、独自の審査/トライアルユーザーフローを持ちます——フル審査を経ずにテストできるよう
   自分自身を**トライアルユーザー**として追加してください。割り当てられる**LIFF ID**を控え
   （→`LINE_MINIAPP_LIFF_ID`）。
3. この順序を間違える（例: MINI AppチャンネルがMessaging APIチャンネルとは別のプロバイダー
   設定を必要とすると理解する前に登録しようとする）ことが、コード上の何よりも実際に詰まり
   やすいポイントです。

### 起動

```powershell
cd src/LineCompanionBot
$env:LINE_CHANNEL_SECRET       = "<チャネルシークレット>"
$env:LINE_CHANNEL_ACCESS_TOKEN = "<チャネルアクセストークン>"
$env:LINE_MINIAPP_LIFF_ID      = "<LIFF ID>"

dotnet run -- setup   # リッチメニューを作成・有効化——一度だけ実行
dotnet run             # アプリ起動
```

`Line.OpenApi.Samples.Webhook`サンプルと全く同じ手順でdev tunnel経由で公開します:

```powershell
devtunnel user login       # 初回のみ
devtunnel host -p 5091 --allow-anonymous
```

転送されたHTTPS URL + `/webhook`をコンソールのチャネルのWebhook URLに設定し、**Use webhook**を
オンにして**Verify**をクリックします。

### 一連の流れを試す

1. Botを友だち追加すると、リッチメニュー（Feed/Play/Status/Shopの4象限）がすぐに表示される
   はずです——これが`dotnet run -- setup`の効果です。
2. **Feed**/**Play**/**Status**をタップ——それぞれ約1秒以内にFlex Messageのステータスカードが
   返るはずです。Hungerが低い状態（または減衰は実時間なので待って）で**Play**を試すと、
   拒否カードが見られます。
3. **Shop**をタップしてMINI Appを開く——カタログが読み込まれ、アイテムを予約できるはずです。
   実際の購入を完了させるには、`shop.js`（第6章）にTODOとして残したクライアント側IAP SDK呼び
   出しの配線が必要です——まずLINE現行のMINI App IAPドキュメントで正確な呼び出しを確認して
   ください。
4. 購入が完了すると、`PurchaseReconciliationService`が次のポーリングtick
   （`LINE_MINIAPP_POLL_SECONDS`、既定30秒——**即座ではありません**、push webhookが無いため）
   で検出し、新しいアイテムを知らせるチャットメッセージが届くはずです。

### トラブルシューティング

- **リッチメニューが表示されない/タップしても反応しない。** `dotnet run -- setup`が
  （「未設定」メッセージではなく）リッチメニューIDを出力したか確認。`GET /`が
  `messaging: enabled`を報告しているか確認。
- **`/webhook`で401。** `LINE_CHANNEL_SECRET`がチャネルのものと一致していません
  （素の`Line.OpenApi.Samples.Webhook`サンプルと同じ失敗モードです）。
- **Feed/Play/Statusのpostbackが何も反応しない。** アプリログで「Failed to reply to a
  postback event」を確認——たいていはテストが遅すぎてリプライトークン（約1分有効）が期限切れ
  になっているか、`LINE_CHANNEL_ACCESS_TOKEN`が未設定/無効です。
- **Shopボタンが空白/壊れたページを開く。** `LINE_MINIAPP_LIFF_ID`が未設定か誤っているか、
  MINI AppチャンネルのエンドポイントURLがこのアプリの`/shop/`パスを指していません——これは
  コードの問題ではなくMINI App側のコンソール設定の問題です。
- **購入は完了したのにチャットメッセージが届かない。** `LINE_MINIAPP_POLL_SECONDS`まで
  かかるのは想定通りです——IAP完了に即時pushはありません。それでも届かない場合は
  `PurchaseReconciliationService`の警告ログを確認してください（無効/期限切れのチャネル
  トークンが最もよくある原因です）。
- **サービスメッセージが送られず、常にpushにフォールバックする。** `LINE_MINIAPP_TEMPLATE_NAME`
  に*承認済み*テンプレートが設定され、かつユーザーが最近ショップを開いていて生きたnotifier
  トークンが存在する、という両方が揃わない限りこれは想定通りです（第8章参照）。pushへの
  フォールバックはバグではなく、意図された既定の安全経路です。

### 確認済みのこと・実チャネルが無いと確認できないこと

第8章までの内容はすべて、このリポジトリ自身のサンドボックス化された開発環境でローカルに実行
確認済みです: 署名検証（受理・拒否の両方）、`PetGrowthEngine`へのpostback分岐が実際のFlex
Messageを生成すること、`setup`のCLI分岐とトークン未設定時のクリーンな終了、ショップの全
エンドポイント（config/catalog/inventory/reserveの検証分岐）、そして照合サービスのポーリング
＆リトライループが実際に`api.line.me`へ到達し実際の`401`応答をクラッシュせず処理すること。
残っているのは——実際のチャット返信が届くこと、実LINEアプリでリッチメニューが表示されること、
実際に完了したIAP購入が付与→通知の流れ全体を駆動すること——これらには上記の実チャネル設定が
必要です。だからこそ、この章は「動くはずです」を前の章に混ぜ込まず、独立した章として存在して
います。

## レビュー後の改善

このプロジェクトも、`Line.OpenApi.*`ライブラリ本体と同じ3役レビューゲート（コード/セキュリティ/
テスト・アーキ）を通しています——3役とも**CONCERNS（非ブロッキング）**という結果で、実行可能な
指摘は完了扱いにする前に直しました。既に動いていたコードへの改善であり新機能ではないため、
上の各章を書き換えるのではなくここにまとめて記録します:

- **照合処理が、クライアント入力よりLINE自身のイベントデータを信頼するようになった。**
  `PurchaseReconciliationService.PollOnceAsync`は、付与・通知を`order.UserId`（`/api/shop/reserve`
  時にクライアントが送ってきた値）ではなく`ev.UserId`（LINE自身のIAP webhookペイロード）で行い、
  両者が食い違えば警告ログを出します。これは正当性の修正（`MiniAppWebhookEvent`が既に正しい
  ユーザーIDを持っているのに、未検証の方を優先する理由がない）であると同時に、セキュリティ
  レビューで指摘された「`/api/shop/reserve`がクライアント供給の`userId`を信頼している」という
  指摘への具体的な緩和策でもあります: 呼び出し元がその値について嘘をついても、実際の付与と
  チャット通知は本当の購入者に届きます。
- **ポーリング窓に小さな余裕（trailing buffer）を追加。**
  （`PurchaseReconciliationService`の`TrailingBufferSeconds = 5`）——現在時刻ぎりぎりまで問い合わせると、
  直前に完了したもののLINE側でまだインデックスされていないイベントを取りこぼすリスクがありました。
  Grant/RevokeはOrderId起点で冪等なため、このコストはゼロです。
- **`NotifyPurchaseAsync`のフォールバックロジックを厳密化。** `SendServiceMessageAsync`の呼び出し
  自体だけがpushフォールバックをゲートするようにしました——更新トークンの保存は、送信と同じ
  `try`の中に置かなくなりました（以前は、送信ではなく帳簿処理側が例外を投げた場合に二重push
  になるリスクがありました）。サービスメッセージとpushフォールバックの**両方**が失敗した場合は、
  `Warning`ではなく`Error`レベルでログを出すようにしました——アイテムは確かに付与されたのに
  ユーザーには一度も伝わらなかった、ということを意味し、他に何もリトライしないためです。
- **`InventoryStore.Get`が`Grant`/`Revoke`と同じロックの下でスナップショットを取るようになった。**
  `GET /api/shop/inventory/{userId}`から到達可能な一方で、バックグラウンドの照合ループが同じ
  リストを変更しており、読み取り側も同期しないと素の`List<T>`としては安全ではありません。
- **「Golden Kibble」が実際に効果を持つようになった。** カタログの説明は常に「Hungerを満タンまで
  回復」と謳っていましたが、それを消費するものも`PetStore`に触れるものも何もありませんでした
  ——レビュアーが、読者がそれを購入しても何も起きないことを見た、と指摘しました。
  `InventoryStore.TryConsume`と`PetGrowthEngine.FeedRare`でループを閉じました: 未消費のGolden
  Kibbleを持った状態でfeedすると、通常の部分回復の代わりにそれを消費して即座にフル回復します
  （コスメティックアイテムは意図的に引き続きfeed時の効果を持ちません）。
- **`PetGrowthEngine`のテストケースを2件追加**——チュートリアル自身が「網羅している」と主張して
  いたのに実は網羅していなかった穴を閉じました: `Play`のHappiness増加の100クランプと、
  `FeedRare`のフル回復挙動です。
- **コードでは直さず文書化に留めたもの:** `X-Forwarded-For`の信頼ギャップ（信頼できるプロキシの
  検証なし）は、`Program.cs`とREADMEで明記するに留め、コードでは解決していません——正しく検証
  するには本デモが持たない実リバースプロキシ構成が必要であり、このフィールドはLINEへの不正
  利用対策シグナルに過ぎず、このアプリ自身のセキュリティ境界ではないためです。
