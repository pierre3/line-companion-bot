[← 第5章](05-rich-menu.md) | [索引](README.md) | [第7章 →](07-reconciliation.md)

# 第6章 — MINI Appショップ: フロントエンドとバックエンド

**このステップで作るもの:** リッチメニューのショップボタンが開くショップです。`wwwroot/shop`
から配信される素のHTML/JSページ、それを支える`/api/shop`配下の小さなエンドポイント群、そして
残る3つの永続化ストアからなります。あわせてこの章では、第4章から宙ぶらりんになっていた
Golden Kibbleのループも閉じます。

## カタログと、それが必要とするストア

`src/LineCompanionBot/Services/ShopCatalog.cs`は、固定の3アイテムを並べただけのリストです。管理用の
CRUDはありません。あくまでデモだからです:

```csharp
namespace LineCompanionBot.Services;

public sealed record ShopItem(string ProductId, string Name, string Description);

public static class ShopCatalog
{
    public static readonly IReadOnlyList<ShopItem> Items = new List<ShopItem>
    {
        new("rare-food", "Golden Kibble", "A rare treat — refills Hunger to full instantly."),
        new("party-hat", "Party Hat", "A cosmetic hat for your companion."),
        new("star-badge", "Star Badge", "A shiny cosmetic badge to show off."),
    };
    public static ShopItem? Find(string productId) => Items.FirstOrDefault(i => i.ProductId == productId);
}
```

これに伴って、`Persistence/`配下では`IPetStore`に加えて、さらに3つの`I*Store`インターフェースが
仲間入りします。それぞれに`InMemory*`実装を用意します:

- **`IOrderStore`** — 予約時の`RecordAsync(orderId, userId, productId)`と、照合時の
  `TryGetAsync(orderId)`。*このアプリ*がどの注文を開始したのかを記録しておくためのものです。
- **`IInventoryStore`** — `GetAsync`（在庫エンドポイント）、`GrantAsync` / `RevokeAsync`（照合で
  使用、第7章）、そして`TryConsumeAsync`（後述のfeed分岐で使用）を担います。`GrantAsync`は
  `OrderId`をキーにしているので、二度目以降の繰り返しは何もしません。この冪等性こそが、照合を
  安全に何度でも走らせられる根拠です。そのインメモリ実装は、書き込みが使うのと**同じロックの下で**
  読み取りのスナップショットを取ります。`GetAsync`はHTTPリクエストから、同じ`List<T>`を書き換える
  バックグラウンドループと並行して到達し得るからです。
- **`INotifierTokenStore`** — ユーザーごとに最新の`NotifierToken`を保持します（第8章がこれを
  使って送信します）。

実ファイルは、第3章の `IPetStore` / `InMemoryPetStore` と同じ「インターフェース＋インメモリ実装」の
対です。順に作成します。まず `src/LineCompanionBot/Persistence/IOrderStore.cs`:

```csharp
namespace LineCompanionBot.Persistence;

public sealed record ShopOrder(string OrderId, string UserId, string ProductId);

// 予約時（第6章）に記録し、照合時（第7章）に参照する。IAP webhook イベントが、このアプリが実際に
// 開始した注文に対応することを確認するため。
public interface IOrderStore
{
    Task RecordAsync(string orderId, string userId, string productId, CancellationToken ct = default);

    Task<ShopOrder?> TryGetAsync(string orderId, CancellationToken ct = default);
}
```

そのインメモリ実装 `src/LineCompanionBot/Persistence/InMemory/InMemoryOrderStore.cs`:

```csharp
using System.Collections.Concurrent;

namespace LineCompanionBot.Persistence.InMemory;

public sealed class InMemoryOrderStore : IOrderStore
{
    private readonly ConcurrentDictionary<string, ShopOrder> _orders = new();

    public Task RecordAsync(string orderId, string userId, string productId, CancellationToken ct = default)
    {
        _orders[orderId] = new ShopOrder(orderId, userId, productId);
        return Task.CompletedTask;
    }

    public Task<ShopOrder?> TryGetAsync(string orderId, CancellationToken ct = default)
        => Task.FromResult(_orders.TryGetValue(orderId, out var order) ? order : null);
}
```

次に `src/LineCompanionBot/Persistence/IInventoryStore.cs`:

```csharp
namespace LineCompanionBot.Persistence;

public sealed record InventoryItem(string OrderId, string ProductId);

public interface IInventoryStore
{
    Task<IReadOnlyList<InventoryItem>> GetAsync(string userId, CancellationToken ct = default);

    // OrderId をキーにするので、重なったポーリング窓（例: 再起動後）を再スキャンしても同じ購入を
    // 二重付与することはない。これが照合を冪等に走らせられる根拠。
    Task<bool> GrantAsync(string userId, string orderId, string productId, CancellationToken ct = default);

    Task<bool> RevokeAsync(string userId, string orderId, CancellationToken ct = default);

    // 一致するアイテムを1つ消費して取り除く（例: 使い切りのレア餌）。「使用済みフラグ」ではなく
    // 削除で問題ない。ウォーターマークは再起動後に履歴を再スキャンしないので、消費済みアイテムを
    // 再付与する（ゆえに再び探す必要がある）経路は存在しないため。
    Task<bool> TryConsumeAsync(string userId, string productId, CancellationToken ct = default);
}
```

そのインメモリ実装 `src/LineCompanionBot/Persistence/InMemory/InMemoryInventoryStore.cs`。付与/取り消し/
消費と、書き込みと**同じロックの下で**取るスナップショット読み取り:

```csharp
using System.Collections.Concurrent;

namespace LineCompanionBot.Persistence.InMemory;

public sealed class InMemoryInventoryStore : IInventoryStore
{
    private readonly ConcurrentDictionary<string, List<InventoryItem>> _inventory = new();

    // Grant/Revoke と同じロックの下でスナップショットを取る。Get は GET エンドポイントから、同じ
    // List<T> を書き換えるバックグラウンドの照合ループと並行して到達し得る。ロック下の書き込みに対し
    // 同期なしで読むのはスレッドセーフではない。
    public Task<IReadOnlyList<InventoryItem>> GetAsync(string userId, CancellationToken ct = default)
    {
        if (!_inventory.TryGetValue(userId, out var list))
            return Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        lock (list) { return Task.FromResult<IReadOnlyList<InventoryItem>>(list.ToArray()); }
    }

    public Task<bool> GrantAsync(string userId, string orderId, string productId, CancellationToken ct = default)
    {
        var list = _inventory.GetOrAdd(userId, _ => new List<InventoryItem>());
        lock (list)
        {
            if (list.Any(i => i.OrderId == orderId)) return Task.FromResult(false);
            list.Add(new InventoryItem(orderId, productId));
            return Task.FromResult(true);
        }
    }

    public Task<bool> RevokeAsync(string userId, string orderId, CancellationToken ct = default)
    {
        if (!_inventory.TryGetValue(userId, out var list)) return Task.FromResult(false);
        lock (list) { return Task.FromResult(list.RemoveAll(i => i.OrderId == orderId) > 0); }
    }

    public Task<bool> TryConsumeAsync(string userId, string productId, CancellationToken ct = default)
    {
        if (!_inventory.TryGetValue(userId, out var list)) return Task.FromResult(false);
        lock (list)
        {
            var index = list.FindIndex(i => i.ProductId == productId);
            if (index < 0) return Task.FromResult(false);
            list.RemoveAt(index);
            return Task.FromResult(true);
        }
    }
}
```

最後に `src/LineCompanionBot/Persistence/INotifierTokenStore.cs`:

```csharp
using Line.OpenApi.MiniApp.Models;

namespace LineCompanionBot.Persistence;

// ユーザーごとに最新の NotifierToken を保持する。トークンが（再）発行されたり送信で更新されるたびに
// 上書きする。履歴は不要で、使えるのは常に最新のトークンだけ。
public interface INotifierTokenStore
{
    Task SaveAsync(string userId, NotifierToken token, CancellationToken ct = default);

    Task<NotifierToken?> TryGetAsync(string userId, CancellationToken ct = default);
}
```

そのインメモリ実装 `src/LineCompanionBot/Persistence/InMemory/InMemoryNotifierTokenStore.cs`:

```csharp
using System.Collections.Concurrent;
using Line.OpenApi.MiniApp.Models;

namespace LineCompanionBot.Persistence.InMemory;

public sealed class InMemoryNotifierTokenStore : INotifierTokenStore
{
    private readonly ConcurrentDictionary<string, NotifierToken> _tokens = new();

    public Task SaveAsync(string userId, NotifierToken token, CancellationToken ct = default)
    {
        _tokens[userId] = token;
        return Task.CompletedTask;
    }

    public Task<NotifierToken?> TryGetAsync(string userId, CancellationToken ct = default)
        => Task.FromResult(_tokens.TryGetValue(userId, out var token) ? token : null);
}
```

この3つを、`IPetStore`と並べて`AddInMemoryPersistence`に登録します:

```csharp
services.AddSingleton<IPetStore, InMemoryPetStore>();
services.AddSingleton<IOrderStore, InMemoryOrderStore>();
services.AddSingleton<IInventoryStore, InMemoryInventoryStore>();
services.AddSingleton<INotifierTokenStore, InMemoryNotifierTokenStore>();
```

## バックエンドのエンドポイント

`src/LineCompanionBot/Endpoints/ShopEndpoints.cs`を作り、`MapGroup`で`/api/shop`配下にまとめて
いきます（関連するルートが2つ3つを超えてきたら使う、というのがminimal APIの定石です）。あわせて
`Program.cs`では`app.UseDefaultFiles();`→`app.UseStaticFiles();`の順に呼んで`wwwroot/shop/*`を配信し
（前者が `/shop/` を `/shop/index.html` に解決してくれるので、第9章で MINI App のエンドポイントURLを
`/shop/` で指せます）、`app.MapWebhookEndpoint();`の隣に`app.MapShopEndpoints();`を並べておきます。

```csharp
using Line.OpenApi.MiniApp;
using Line.OpenApi.MiniApp.Models;
using LineCompanionBot.Persistence;
using LineCompanionBot.Services;
using Microsoft.Extensions.Logging;

namespace LineCompanionBot.Endpoints;

public static class ShopEndpoints
{
    public static void MapShopEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/shop");

        group.MapGet("/config", (CompanionSettings settings) => Results.Ok(new { liffId = settings.LiffId }));
        group.MapGet("/catalog", () => Results.Ok(ShopCatalog.Items));
        group.MapGet("/inventory/{userId}", async (string userId, IInventoryStore inventory, CancellationToken ct) =>
            Results.Ok(await inventory.GetAsync(userId, ct)));

        group.MapPost("/reserve", async (
            ShopReserveRequest req, CompanionSettings settings, MiniAppClient miniApp,
            IOrderStore orderStore, INotifierTokenStore notifierTokens, HttpContext http, CancellationToken ct) =>
        {
            if (!settings.HasMessaging)
                return Results.Problem("LINE_CHANNEL_ACCESS_TOKEN is not configured.", statusCode: 503);
            if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.ProductId) || string.IsNullOrWhiteSpace(req.LiffAccessToken))
                return Results.Problem("userId, productId, and liffAccessToken are required.", statusCode: 400);

            var item = ShopCatalog.Find(req.ProductId);
            if (item is null) return Results.Problem($"Unknown productId '{req.ProductId}'.", statusCode: 404);

            // ベストエフォート。notifier 系エンドポイントは stateless/short-lived なトークンを要求するが、
            // このアプリの単一チャネルトークンはそうとは限らない。ここで失敗しても第8章が push にフォール
            // バックするだけで、致命的ではない。
            try
            {
                var notifierToken = await miniApp.IssueNotificationTokenAsync(settings.ChannelAccessToken!, req.LiffAccessToken);
                if (notifierToken is not null) await notifierTokens.SaveAsync(req.UserId, notifierToken, ct);
            }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Failed to issue a notifier token for {UserId}; will fall back to push.", req.UserId); }

            var clientIp = http.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            if (string.IsNullOrEmpty(clientIp)) clientIp = http.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

            IapReserveResult? reserved;
            try
            {
                reserved = await miniApp.ReserveProductAsync(req.LiffAccessToken, clientIp, req.ClientOs ?? "android", item.ProductId, item.Name);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Failed to reserve product {ProductId} for {UserId}.", item.ProductId, req.UserId);
                return Results.Problem("Failed to reserve the purchase with LINE.", statusCode: 502);
            }
            if (reserved?.OrderId is null) return Results.Problem("LINE did not return an order id.", statusCode: 502);

            // ct ではなく CancellationToken.None。LINE は既に注文をコミットしているので、ここでクライアントが
            // 切断してもこの記録を落としてはならない。この書き込みが成立して初めて、照合は後続の
            // purchaseComplete をユーザー/商品に結び付けられる。
            await orderStore.RecordAsync(reserved.OrderId, req.UserId, item.ProductId, CancellationToken.None);
            return Results.Ok(new { orderId = reserved.OrderId });
        });
    }
}

public sealed record ShopReserveRequest(string UserId, string ProductId, string LiffAccessToken, string? ClientOs);
```

いくつか、触れておきたい点があります:

- **通知トークンの発行を、購入完了時ではなくここで行っている。** `IssueNotificationTokenAsync`は
  LIFFアクセストークンを必要としますが、それを持っているのはフロントエンドだけ、しかもユーザーが
  ショップを操作している間だけです。だからここで保存しておくことで、購入が実際に完了する
  数秒〜数十秒後に、第7章がそれを使えます。この処理はreserve呼び出しとは別の、独立したtry/catchで
  包んでいます。通知トークンの発行が失敗しても（これは十分にあり得ます）、購入自体はそのまま
  進まなければならないからです。
- **既知の、そして文書化された2つの簡略化（いずれもコードとREADMEに明示してあります）。** まず、
  `req.UserId`は供給された値をそのまま信頼しています。`Line.OpenApi.MiniApp`には、LIFFトークンから
  それをサーバ側で検証する呼び出しが無いためです。ただし、これが影響するのはローカルな帳簿処理
  だけです。第7章はLINE*自身*のIAP webhookペイロードにある`userId`で付与・通知を行うため、
  呼び出し元が実際の購入の付与先を別の場所へすり替えることはできません。もう一つ、
  `X-Forwarded-For`は信頼できるプロキシの許可リストに対して検証していないので（`UseForwardedHeaders`
  は使っていません）、`clientIp`は検証済みの値ではなく、あくまでLINEへ渡すベストエフォートな
  不正利用対策シグナルとして扱ってください。

## フロントエンド

ここの静的3ファイル（`index.html` / `shop.js` / `shop.css`）は、以下では要点だけを抜粋します。完全な
ファイルは参照リポジトリの
[`src/LineCompanionBot/wwwroot/shop/`](https://github.com/pierre3/line-companion-bot/tree/main/src/LineCompanionBot/wwwroot/shop)
からそのままコピーしてください。バックエンドと違い、ここは `dotnet build` に影響しない静的アセットで、
ページを実際に開くのは[第9章](09-end-to-end.md)です:

```powershell
New-Item -ItemType Directory -Force src/LineCompanionBot/wwwroot/shop | Out-Null
Copy-Item "path/to/line-companion-bot/src/LineCompanionBot/wwwroot/shop/*" src/LineCompanionBot/wwwroot/shop/
```

`wwwroot/shop/index.html`は、LINEのCDNからLIFF SDKを、そしてローカルの2ファイルを読み込みます。
まずはここから見ていきましょう:

```html
<script charset="utf-8" src="https://static.line-scdn.net/liff/edge/2/sdk.js"></script>
...
<p id="status">Loading…</p>
<ul id="catalog"></ul>
<script src="shop.js"></script>
```

`wwwroot/shop/shop.js`は、サーバから受け取った`liffId`でLIFFを初期化し、カタログを描画し、
購入を駆動します。リクエストボディは、バックエンドでは導出できない情報をフロントエンドが供給する
場所だと考えてください:

```js
await liff.init({ liffId: config.liffId });
if (!liff.isLoggedIn()) { liff.login(); return; }

const iapAvailable = liff.isApiAvailable('iap');   // false なら Buy ボタンは無効で描画される
// ...Buy クリック時:
const profile = await liff.getProfile();            // -> userId
const reserveResponse = await fetch('/api/shop/reserve', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    userId: profile.userId,
    productId: item.productId,
    liffAccessToken: liff.getAccessToken(),         // ReserveProductAsync が必要とするトークン
    clientOs: liff.getOS(),                         // "ios" | "android"（UA より信頼できる）
  }),
});
const { orderId } = await reserveResponse.json();

await liff.iap.requestConsentAgreement();           // 同意済みなら何もしない
await liff.iap.createPayment({ productId: item.productId, orderId }); // ストアの購入 UI を起動する
```

ここにはクライアント側の安全策が2つ入っていて、どちらも実際に施した修正です:

- **`isApiAvailable('iap')`を、Buyの描画前と、さらにクリックハンドラの中でもう一度、二重にチェック
  する。** `reserve`はLINE側で実際の注文をコミットしてしまいます。そもそも購入を完了できない
  クライアントのために、そのコミットを消費する意味はありません。だから事前チェックの段階で、
  ボタンを最初から無効にしておきます。
- **Buyボタンは、reserve → consent → createPayment のシーケンス全体を通じて無効化しておく。**
  各ステップはいずれもLINE側の実際のコミットを生成、あるいは消費します。もしシーケンスの途中で
  2回目のクリックが入ると、同じアイテムに対して*2つ目*の注文を予約してしまい、しかも最初の注文を
  キャンセルする手段がありません。ボタンは`finally`で再び有効化されます。

なお、このIAPの呼び出しシーケンスは、`Line.OpenApi.*`がラップしているものではありません。
LIFF SDKの`iap`名前空間にある、クライアント側の、MINI App固有のJavaScriptです（LINE公式の
MINI App IAPドキュメントで確認済みです）。`createPayment`はキャンセルや失敗のときに例外を
投げますが、ショップ側はそのエラーを表面化させて、ユーザーに再試行してもらうだけにとどめて
います。第7章は実際に完了した注文にしか在庫を付与しないからです。

> **既知の制約:** `reserve`が既に成功した後になって`createPayment`がキャンセル/失敗した場合、
> `IOrderStore`のエントリとLINE側の予約済み注文は、どちらもクリーンアップされずに残ります。
> `Line.OpenApi.MiniApp`に予約解放の呼び出しが無いためです。実害はありません（照合は
> `purchaseComplete`まで到達した注文にしか作用しないので）が、使われない記録が1つ取り残されます。
> ここでは回避策を実装するのではなく、READMEに文書化するにとどめています。

## Golden Kibble のループを閉じる

ここまでで Golden Kibble を所有できるようになりました。そこで第4章の`feed`分岐
（`WebhookEndpoints.cs`）をアップグレードして、この Golden Kibble を消費させます。ハンドラの
パラメータに`IInventoryStore inventory`を、`IPetStore petStore` の次、`CancellationToken ct` の直前に
足して（第4章で追加済みの `using LineCompanionBot.Persistence;` に含まれる型なので、新しい using は
不要です）、分岐を書き換えます:

```csharp
case "action=feed":
    // 購入したレア餌を消費して一気に満タンまで回復する。装飾品には feed の効果はない。
    // 両方の呼び出しで CancellationToken.None を使う。TryConsumeAsync は true を返した瞬間にアイテムを
    // 取り除くので、その間にキャンセルが割り込んで対応する SaveAsync を飛ばしてはならない。さもないと
    // アイテムだけ消費されて何も付与されない。
    var decayedBeforeFeed = PetGrowthEngine.ApplyDecay(pet, now);
    pet = await inventory.TryConsumeAsync(userId, "rare-food", CancellationToken.None)
        ? PetGrowthEngine.FeedRare(pet, now)
        : PetGrowthEngine.Feed(pet, now);
    await petStore.SaveAsync(pet, CancellationToken.None);
    // 実際に回復した Hunger（クランプ後）を表示する。名目上の値ではない。
    reply = PetFlexMessageFactory.BuildStatus(
        pet,
        await RareFoodCountAsync(inventory, userId, ct),
        new CareFeedback((int)Math.Round(pet.Hunger - decayedBeforeFeed.Hunger), 0));
    break;
```

ここが、第3章で用意した`FeedRare`がようやく活きる場所です。カタログはGolden Kibbleを
「Hungerを満タンまで回復」と*説明していた*のに、実際にはそれを消費する経路がどこにも無く、
購入しても何の効果も見られない、とレビュアーに指摘されていました。今は、1つ持った状態でfeedすれば、
それをきちんと消費してフル回復します。

あわせて、カードに**レア餌の所持数**を出せるようにします。第4章では在庫がまだ無かったため
`BuildStatus` に `rareFoodCount: 0` を渡していました。ここで所持数を数える小さなヘルパーを
`WebhookEndpoints` に足し（`MapWebhookEndpoint` の隣）、`play`/`status` 側の `rareFoodCount: 0` も
`await RareFoodCountAsync(inventory, userId, ct)` に置き換えます:

```csharp
// カードに表示する、ユーザーが今持っている消費型「rare-food」（Golden Kibble）の個数。
private static async Task<int> RareFoodCountAsync(IInventoryStore inventory, string userId, CancellationToken ct)
{
    var items = await inventory.GetAsync(userId, ct);
    return items.Count(i => i.ProductId == "rare-food");
}
```

これで Golden Kibble を持っているあいだは、どのアクションのカードにもその個数（`🍖 Golden Kibble ×N`）が
表示されます。

## 動かしてみる — バックエンドの契約を検証する

ショップの登録が有効になるよう、プレースホルダーのLIFF idを設定します。あわせて
`LINE_CHANNEL_ACCESS_TOKEN` も必要です。`/reserve` はこれが無いと、下のフィールド/商品検証に進む*前*に
**503** で短絡するためです。ただし[第4章](04-flex-postback.md)で入れた `demo-token` が user-secrets に
残っていればそれで足ります。設定してF5します:

```powershell
dotnet user-secrets set LINE_MINIAPP_LIFF_ID "1234567890-abcdefgh" --project src/LineCompanionBot
# 第4章のダミー。user-secrets を消していたら再設定:
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "demo-token" --project src/LineCompanionBot
```

```powershell
Invoke-RestMethod http://localhost:5091/api/shop/catalog
# -> 3アイテムのカタログ（Golden Kibble / Party Hat / Star Badge）

Invoke-RestMethod http://localhost:5091/api/shop/reserve -Method Post -ContentType 'application/json' -Body '{}'
# -> 400（必須フィールドが不足）

Invoke-RestMethod http://localhost:5091/api/shop/reserve -Method Post -ContentType 'application/json' `
    -Body '{"userId":"U1","productId":"unknown-item","liffAccessToken":"fake"}'
# -> 404（未知の productId）
```

catalog/config/inventoryの各エンドポイントがきちんと応答し、reserveエンドポイントの検証分岐が、
ネットワーク呼び出しよりも*前*の段階で発火します。ただし、実際のショップページを開くには、
本物のMINI AppチャンネルとLIFFランタイムが必要です。[第9章](09-end-to-end.md)で扱います。
