[← 第8章](08-notify.md) | [索引](README.md)

# 第9章 — 実チャネルでのエンドツーエンドとトラブルシューティング

これまでの各章では、それぞれの部品をローカルで確かめてきました。署名のラウンドトリップ、postbackの
ディスパッチ、Flexの構築、ショップのHTTP契約、ポーリングと再試行のループ、いずれも実LINEチャネル
抜きでです。この章はその残りの部分にあたります。すべてを一緒に動かすために実チャネルをつなぐこと、
そして正直なところ、何がうまくいかなくなりがちか、という話です。

## コンソール設定 — 行き詰まりを避ける順序で

1. **Messaging APIチャネルを作成する。**
   [LINE Developers Console](https://developers.line.biz/console/) で作成し、**channel secret** を
   控えたうえで、**channel access token** を発行します（後述の `line webhook` / `line richmenu`
   コマンドはこのトークンを使います）。
2. **LINE MINI Appチャネルを作成する。** 同じプロバイダーの下に作成します。これは通常のLIFFアプリ
   とは別個のプロダクトで、独自の審査 / トライアルユーザーのフローを持っています。full review なしで
   テストできるよう、自分自身を **trial user** として追加しておいてください。割り当てられた
   **LIFF ID** を控え、**このチャネルの channel access token も** 発行しておきます。MINI App を支える
   LIFF アプリは *この* チャネルの配下にあるため、後述の `line liff` コマンドは Messaging チャネルでは
   なく、このトークンを必要とします。
3. 実はこの順序を取り違えること、つまりMINI Appチャネルが、Messaging APIチャネルとは別個の独自
   プロバイダー設定を必要とする点を理解する前に登録しようとしてしまうことが、ここでいちばん起こり
   やすい現実のつまずきどころです。コードの中のどんな落とし穴よりも、です。

エンドポイントURLをコンソールで設定する作業は、もう **ありません**。LINE側の2つのURL——Messaging
チャネルの **Webhook URL** と MINI App の **エンドポイントURL**——はどちらも、dev tunnel を用意した後に
下の「立ち上げる」で `line` ツールから設定します。コンソールでのクリックが残るのは、一度きりの
**Use webhook** トグルだけです（そこで触れます）。

## シークレットを user-secrets に入れる

Getting started でも触れたとおり、シークレットは `dotnet user-secrets` に置き、チェックインされる
ファイルには決して置きません:

```powershell
dotnet user-secrets set LINE_CHANNEL_SECRET       "<channel secret>"       --project src/LineCompanionBot
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "<channel access token>" --project src/LineCompanionBot
dotnet user-secrets set LINE_MINIAPP_LIFF_ID      "<liff id>"              --project src/LineCompanionBot
# 任意。push ではなくサービスメッセージ経路を使う場合:
# dotnet user-secrets set LINE_MINIAPP_TEMPLATE_NAME "<approved template name>" --project src/LineCompanionBot
```

`BuildCompanionConfiguration` が user-secrets を読むのは `Development` 環境のときだけで、F5の起動構成が
`ASPNETCORE_ENVIRONMENT=Development` を設定するので、アプリはこれらを拾います。（下記の `line` ツールは
user-secrets ではなく、環境変数 / `--channel-token` / `line config` プロファイルからチャネルアクセス
トークンを読みます。しかも *2つ* 必要です。webhook / リッチメニューのコマンドには Messaging チャネルの
トークン、LIFF のコマンドには MINI App チャネルのトークンです。アプリ自体が保持するのは
`LINE_CHANNEL_ACCESS_TOKEN` の1つだけで、MINI App のトークンは `line liff` に直接渡します。）

## 立ち上げる

リッチメニューの登録は一度きり。LINEをアプリに向ける作業は、トンネルURLが変わるたびに `line`
コマンドを数本流すだけで、コンソールの往復はありません。

### 一度きり — リッチメニューを登録する

**リッチメニューを一度だけ作成・設定します**（[第5章](05-rich-menu.md)）。先に `richmenu.json` の
`YOUR_LIFF_ID` を自分の LIFF id へ置き換えます。この `https://liff.line.me/<LIFF_ID>` というURLは
*恒久的*で（LIFFアプリの現在のエンドポイントへリダイレクトします）、トンネルが変わってもメニュー側は
一切触る必要がありません。ツールには **Messaging** チャネルのトークンを渡し（user-secrets は
読みません）、3ステップを実行します。`create` が出力する id を次の2つに渡します:

```powershell
$env:LINE_CHANNEL_ACCESS_TOKEN = "<messaging channel access token>"
line richmenu create --file src/LineCompanionBot/assets/richmenu.json   # 新しい id が出力される
line richmenu image  <richMenuId> --file src/LineCompanionBot/assets/richmenu.png
line richmenu set-default <richMenuId>
```

### アプリを起動してトンネルを開く

1. **アプリを起動する:** **F5** を押すだけです。
2. **devトンネルで公開する:** LINEがこちらのwebhookとショップページの両方に到達できるようにします:

   ```powershell
   devtunnel user login       # 初回のみ
   devtunnel host -p 5091 --allow-anonymous
   ```

   出力される転送先のHTTPSベースURLを控えます（以下では `https://<tunnel>` と表記します）。

### LINEをトンネルに向ける — すべてCLIで

`line` ツールがLINE側の2つのURLを両方とも設定するので、ここでコンソールを開く必要はありません。
注意点は2つ。両者は **別々のチャネル** に属するため各コマンドには *そのチャネルの* アクセストークンが
必要なこと、そしてパスが違うこと——webhook は `/webhook`、MINI App（LIFF）のエンドポイントは
`/shop/` です。

```powershell
# 1. Webhook URL — Messaging チャネルのトークン。（devtunnel は端末を占有し、再セッションでは
#    一度きりのリッチメニュー手順を飛ばすので、引き継がれている前提にせずここでセットする。）
$env:LINE_CHANNEL_ACCESS_TOKEN = "<messaging channel access token>"
line webhook set-endpoint --url https://<tunnel>/webhook
line webhook test-endpoint          # LINE がエンドポイントを叩いて到達性を報告。コンソールの「Verify」の代わり

# 2. MINI App エンドポイント — LIFF アプリは MINI App チャネル配下なので *そのチャネルの* トークンを渡す。
line liff list        --channel-token "<mini app channel access token>"   # liffId を調べる
line liff update-url <liffId> --url https://<tunnel>/shop/ --channel-token "<mini app channel access token>"
```

> **「Use webhook」は一度きりのコンソール操作です。** `set-endpoint` はURLを設定しますが、チャネルの
> *Use webhook* スイッチは切り替えません（Messaging API が公開していないためです）。`line webhook
> get-endpoint` を実行し、`active: false` と出たら、コンソールで **Use webhook** を一度だけオンに
> します。残っているコンソール操作はこれだけで、しかもチャネルごとに一度きり。以降のセッションは、
> 新しいトンネルURLに対して上の2コマンドを流すだけです。

トンネルを再起動するたびに転送先URLは変わるので、`webhook set-endpoint` と `liff update-url` を新しい
ホストで流し直します。2コマンドで済み、コンソールは不要です。

## フルループを試す

1. Botを友だち追加します。すると、リッチメニュー（Feed / Play / Status / Shop）が即座に表示される
   はずです。`line richmenu set-default` が効いている証拠です。
2. **Feed / Play / Status** をタップしてみます。それぞれ約1秒でFlexのステータスカードが返ってきます。
   Hungerが低い状態（減衰はリアルタイムです）で **Play** をタップすると、例の拒否カードが表示されます。
3. **Shop** をタップしてMINI Appを開きます。カタログが読み込まれ、アイテムを購入できます
   （`liff.iap.createPayment` が、実際のApp Store / Play Storeの購入UIを駆動します。第6章を参照）。
4. 購入が完了すると、`PurchaseReconciliationService` が次のポーリングtickでそれを拾い上げ
   （`LINE_MINIAPP_POLL_SECONDS`、デフォルト30秒。ここは **即時ではありません**。push webhookが無い
   ためです）、新しいアイテムを知らせるチャットメッセージが届きます。試しに **Golden Kibble** を
   買ってから **Feed** をタップすると、Hungerが満タンまで回復し、そのぶん消費されるのが分かります。

なお、この間ずっとVS Codeのデバッガをアタッチしたままにしておけます。`WebhookEndpoints` や
`PurchaseReconciliationService` にブレークポイントを置いて、実際のLINEトラフィックが流れていく様子を
じっくり観察できます。

## 開発時のみ: 購入をシミュレートする

*本物の*購入をエンドツーエンドで通すことは、ローカルではできません。LINE MINI App のアプリ内課金は、
テスト決済すら**IAP審査の承認**（数週間・日本限定・事業者向け）が前提で、しかもそれは開発用チャネルに
登録したテスターでのみ動きます。さらに、ユーザーが実際に課金できるようになるには別途「認証審査」も必要
です。それらが揃うまでは **Buy は無効**（`liff.isApiAvailable('iap')` が false）で、照合ポーリングは
`403` をログに出します。これはバグではなく想定どおりの状態です。

そこで、その先の**下流フロー**（付与 → チャット通知 → Feed での Golden Kibble 消費）だけは検証できる
よう、アプリは購入完了の代役となる**開発時限定**のフックを用意しています。これは**環境が Development の
ときだけ**マップされます。Production ではエンドポイントは `404` を返し、`config.devPurchaseEnabled` は
`false` になるので、デプロイ後のアプリには一切含まれません。

> **注意: この dev フックは無認可です。** 認証チェックなしで、任意の `userId` に在庫を付与し push を
> 送れます。`localhost` なら無害ですが、上の起動手順は Development サーバを `devtunnel …
> --allow-anonymous` で公開します。トンネルが開いている間は、URL を知っている第三者もこのエンドポイント
> に到達できます（既知の userId への push スパム等）。トンネルは短時間に留め、URL を共有せず、テストが
> 終わったら閉じてください。Production には存在しません。

`ShopEndpoints.cs` に次を足します（using が3本増えます: `Line.OpenApi.Messaging`、
`Line.OpenApi.Messaging.Generated.Api.Models`、`Microsoft.AspNetCore.Mvc`）。環境を一度だけ取得し、
`/config` で公開し、`isDev` ガードの内側にエンドポイントをマップします:

```csharp
var isDev = app.Environment.IsDevelopment();

group.MapGet("/config", (CompanionSettings settings) =>
    Results.Ok(new { liffId = settings.LiffId, devPurchaseEnabled = isDev }));

// ...第6章の /reserve エンドポイント...

if (isDev)
{
    // 完了した IAP 購入の代役。アイテムを付与し、purchaseComplete イベント時に
    // PurchaseReconciliationService が送るのと同じ push を送る。LINE の IAP エンドポイントには触れない
    // ので、isApiAvailable('iap') が false でも動く。マップされるのは Development のときだけ。
    group.MapPost("/dev/complete-purchase", async (
        DevCompletePurchaseRequest req,
        [FromServices] MessagingClient? messaging,
        IOrderStore orderStore,
        IInventoryStore inventory,
        CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.ProductId))
            return Results.Problem("userId and productId are required.", statusCode: 400);

        var item = ShopCatalog.Find(req.ProductId);
        if (item is null)
            return Results.Problem($"Unknown productId '{req.ProductId}'.", statusCode: 404);

        var orderId = $"dev-{Guid.NewGuid():N}";
        await orderStore.RecordAsync(orderId, req.UserId, item.ProductId, ct);
        var granted = await inventory.GrantAsync(req.UserId, orderId, item.ProductId, ct);

        if (granted && messaging is not null)
        {
            try
            {
                await messaging.Api.V2.Bot.Message.Push.PostAsync(new PushMessageRequest
                {
                    To = req.UserId,
                    Messages = new List<Message> { new TextMessage { Type = "text", Text = $"You received: {item.Name}!" } },
                }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Dev complete-purchase: push failed for {UserId}.", req.UserId);
            }
        }

        return Results.Ok(new { orderId, granted, notified = granted && messaging is not null });
    });
}
```

リクエストのレコードは `ShopReserveRequest` の隣に:

```csharp
public sealed record DevCompletePurchaseRequest(string UserId, string ProductId);
```

`shop.js` は `config.devPurchaseEnabled` が true のとき、各アイテムの隣に **Mark purchased (dev)**
ボタンを描画します（Buy が無効でも押せます）:

```js
if (devPurchaseEnabled) {
  const devButton = document.createElement('button');
  devButton.textContent = 'Mark purchased (dev)';
  devButton.addEventListener('click', () => devComplete(item, devButton));
  li.appendChild(devButton);
}

async function devComplete(item, button) {
  button.disabled = true;
  try {
    const profile = await liff.getProfile();
    const res = await fetch('/api/shop/dev/complete-purchase', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userId: profile.userId, productId: item.productId }),
    });
    if (!res.ok) { statusEl.textContent = `Dev grant failed for ${item.name} (HTTP ${res.status}).`; return; }
    const result = await res.json();
    statusEl.textContent = result.notified
      ? `Dev: granted ${item.name}. Check the chat for the notification, then tap Feed.`
      : `Dev: granted ${item.name} (no push — LINE_CHANNEL_ACCESS_TOKEN is unset). Tap Feed.`;
  } catch (err) { statusEl.textContent = `Dev grant error: ${err.message ?? err}`;
  } finally { button.disabled = false; }
}
```

**Golden Kibble** の **Mark purchased (dev)** を押す（または下の `curl` を直接叩く）と、チャットに
「You received: Golden Kibble!」の push が届き、`GET /api/shop/inventory/{userId}` にアイテムが入り、
次に **Feed** をタップすると Kibble が消費されて Hunger が満タンまで回復するはずです。LINE の課金処理
そのもの以外、本物の `purchaseComplete` が起こすことをすべて再現できます:

```powershell
curl -X POST http://localhost:5091/api/shop/dev/complete-purchase `
    -H 'Content-Type: application/json' -d '{"userId":"<自分のuserId>","productId":"rare-food"}'
```

## トラブルシューティング

- **リッチメニューが出ない / タップしても何も起きない。** まず、`line richmenu set-default` が成功
  していること（`line richmenu get-default` が id を返すこと）を確かめてください。そのうえで `GET /` が
  `messaging: enabled` を報告しているかも見ておきます。
- **`/webhook` で401。** これは `LINE_CHANNEL_SECRET` がチャネルのものと一致していないサインです。
- **Webhookイベントが一向に届かない。** `line webhook get-endpoint` が *現在の* トンネルURLを
  `active: true` で表示し、`line webhook test-endpoint` が `success: true` を返すはずです。前回の
  トンネルセッションの古いURL（`set-endpoint` のやり直し忘れ）か、`active: false`（一度きりの
  **Use webhook** トグルがまだオフ）が、たいていの原因です。
- **Feed/Play/Status が何もしない。** ログに "Failed to reply to a postback event" が出ていないか
  確認します。たいていは、テストがもたついて期限切れになったreplyトークン（有効期間は約1分です）か、
  欠落した/無効なアクセストークンが原因です。
- **Shopボタンが空白ページを開く / 反応しない。** `richmenu.json` の `YOUR_LIFF_ID` を置換しないまま
  メニューを作成したか、`LINE_MINIAPP_LIFF_ID` が間違っているか、LIFFアプリのエンドポイントURLが
  このアプリの `/shop/` パスを向いていないかのいずれかです。現在のURLは `line liff list` で確認し、
  `line liff update-url <liffId> --url https://<tunnel>/shop/` で向け直します（どちらも **MINI App**
  チャネルのトークンが必要です）。前回セッションの古いトンネルURLが、たいていの原因です。
  `YOUR_LIFF_ID` 自体を変えた場合は、`line richmenu create`/`image`/`set-default` をやり直します。
- **購入は完了するのにチャットメッセージが来ない。** 最大で `LINE_MINIAPP_POLL_SECONDS` ほどかかるのが
  想定どおりの挙動です。即時pushは無いのでした。それでも一向に届かないようなら、
  `PurchaseReconciliationService` のwarningを確認してください（無効/期限切れのトークンが、たいていの
  原因です）。
- **サービスメッセージが一度も送られず、常にPushへフォールバックする。** これは `LINE_MINIAPP_TEMPLATE_NAME`
  が *承認済み* テンプレートであること、かつユーザーが有効なnotifierトークンを取れるほど最近ショップを
  開いていること。この両方が揃わない限り、むしろ想定どおりの挙動です（第8章を参照）。Pushフォール
  バックは意図されたデフォルトの安全な経路であって、バグではありません。

## 確認済みのこと・実チャネルが必要なこと

実のところ、第8章までのすべてはローカルで確認できます。署名検証（受理 *と* 拒否の両方）、
`PetGrowthEngine` へのpostbackディスパッチが実際のFlex Messageを生成すること、すべてのショップ
エンドポイント（config/catalog/inventory
に reserve のバリデーション分岐まで）、そして照合ループが実際に `api.line.me` に到達し、クラッシュせず
にレスポンスを処理すること。ここまでは手元で見届けられます。残るのは、チャット返信が実際に届くこと、
リッチメニューがレンダリングされること、完了したIAP購入が grant→notify の全経路を駆動すること。これらには
上記の実チャネル設定がどうしても必要です。だからこそこの章は、「信じてくれ、動くから」を前の各章
に畳み込むのではなく、あえて別立てで存在しています。

## レビューゲートについての注記

最後に一つ。このアプリを完成とみなす前に、3役のレビューゲート（コード / セキュリティ / テスト・アーキ）
を通しています。いずれも **CONCERNS（非ブロッキング）** を返し、対応可能な指摘はすべて修正済みです。
そしてそれらの修正は、別立ての付録にまとめたのではなく、それぞれが関係する章の中に畳み込んであります。
LINE自身の `ev.UserId` を信頼する照合（第7章）、引き締めたnotifyフォールバックとその `Error` レベルの
二重失敗ログ（第8章）、インベントリの読み取りロックと、アイテムに実際の効果を持たせるGolden Kibbleの
消費（第6章および第3〜4章）、そして追加した `PetGrowthEngine` のテストケース（第3章）といった具合です。
つまりここまでで組み上げたコードは *そのまま* レビュー済みの最終形であり、あとから学び直さなければ
ならないものは、何一つ残っていません。
