[← 第6章](06-shop.md) | [索引](README.md) | [第8章 →](08-notify.md)

# 第7章 — 購入照合

**このステップで作るもの:** `PurchaseReconciliationService` です。完了した購入を検知して、対応する
アイテムをユーザーに付与する `BackgroundService`——第6章の `reserve` 呼び出しで開いたループを、
ここでようやく閉じます。

さて、正直に言うと、ここはこのシステムでいちばん「ぎこちない」部分かもしれません。理由はシンプルで、
`MiniAppClient` にはIAPイベント用のpush webhookが存在しないのです（第2章のMessaging webhookとは
対照的ですね）。用意されているのは `GetWebhookEventsAsync`、つまり7日窓・カーソルページングの
*pull* API だけ。だから「完了した瞬間に受け取る」わけにはいかず、こちらから定期的に
「前回チェック以降、何が起きた?」と問いに行くことになります。`LINE_MINIAPP_POLL_SECONDS`
（デフォルト30秒）のタイマーで刻む、いわゆるポーリングです。

## 登録の仕方と、ライフタイムの機微

まずは `Program.cs` に一行:

```csharp
builder.Services.AddHostedService<PurchaseReconciliationService>();
```

ここで少し立ち止まりたいのがライフタイムの話です。`BackgroundService` はプロセスが生きている間
ずっと存在する **Singleton**。一方で `InMemory*` ストアが今日 Singleton なのは、実は *たまたま*
にすぎません。もし将来これをRDBバックのストアに差し替えたら、そちらは通常 **Scoped**（作業単位
ごとに `DbContext` を1つ）になります。そして Singleton は Scoped 依存をそのまま抱え込めません
——「captive dependency（捕捉された依存）」問題、最初に生成された `DbContext` を永久に握りしめて
しまう、あれです。

そこでこのサービスは、ストアを直接コンストラクタで受け取るのではなく `IServiceScopeFactory` を
受け取り、ポーリング1回ごとに新しいスコープからすべてを解決します。こうしておけば、あとから
ストアのライフタイムを変えても、このクラスには一切手を入れずに済む——第3章でわざわざ永続化の
シーム（seam）を設けておいたのは、まさにこの瞬間のためでした。

## 完全なファイル

`src/LineCompanionBot/Services/PurchaseReconciliationService.cs` の全体です。以降の2節で
`ExecuteAsync` と `PollOnceAsync` を順に読み解きます。`NotifyPurchaseAsync`（付与直後の通知）は、
サービスが単体でビルド・完結できるよう、ここではスタブに留めてあります——その中身（と、そこで必要に
なる using 2本）は第8章で実装します:

```csharp
using Line.OpenApi.Messaging;
using Line.OpenApi.MiniApp;
using LineCompanionBot.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LineCompanionBot.Services;

// There is no push webhook for IAP events — GetWebhookEventsAsync must be polled. Idempotent by
// design: IInventoryStore.Grant/Revoke key off OrderId, so re-scanning an overlapping window after
// a restart can never double-grant or double-revoke.
public sealed class PurchaseReconciliationService : BackgroundService
{
    private readonly CompanionSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PurchaseReconciliationService> _logger;
    private long _watermarkEpochSeconds;

    public PurchaseReconciliationService(
        CompanionSettings settings,
        IServiceScopeFactory scopeFactory,
        ILogger<PurchaseReconciliationService> logger)
    {
        _settings = settings;
        // Stores are resolved per-poll from a fresh DI scope (see PollOnceAsync) rather than taken
        // as direct constructor dependencies: this BackgroundService is a Singleton for the
        // process lifetime, but the I*Store implementations only happen to be Singleton today
        // (in-memory). A future RDB-backed store would typically be Scoped (per-request/per-unit-
        // of-work DbContext), and a Singleton can't hold a Scoped dependency directly (the
        // "captive dependency" problem) — resolving via scope here means that swap needs no change
        // in this class.
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Only purchases made from this point on are polled for — a fresh demo process has no
        // reason to re-scan the full 7-day history on every restart.
        _watermarkEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.HasMessaging)
        {
            _logger.LogInformation("LINE_CHANNEL_ACCESS_TOKEN is not set — purchase reconciliation is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.PollSeconds));
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // A poll failure must not kill the loop — just retry on the next tick.
                _logger.LogWarning(ex, "Purchase reconciliation poll failed; will retry next tick.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Trailing safety margin: querying right up to the current instant risks missing an event
    // that completed moments ago but isn't indexed yet on LINE's side. A few seconds of overlap
    // costs nothing (Grant/Revoke are idempotent by OrderId) but closes that gap.
    private const int TrailingBufferSeconds = 5;

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var miniApp = services.GetRequiredService<MiniAppClient>();
        var messaging = services.GetRequiredService<MessagingClient>();
        var orders = services.GetRequiredService<IOrderStore>();
        var inventory = services.GetRequiredService<IInventoryStore>();
        var notifierTokens = services.GetRequiredService<INotifierTokenStore>();

        var start = _watermarkEpochSeconds;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - TrailingBufferSeconds;
        string? cursor = null;

        // Advance the watermark only after a fully successful walk of every page — advancing
        // per-event would risk silently skipping the rest of a page if this loop is interrupted.
        do
        {
            var page = await miniApp.GetWebhookEventsAsync(
                _settings.ChannelAccessToken!, start, now, pageSize: 50, cursor: cursor, status: "SUCCESS", ct);

            foreach (var entry in page?.Events ?? new())
            {
                var ev = entry.Event;
                if (ev?.OrderId is null)
                {
                    continue;
                }

                // Only act on orders this app itself reserved — other IAP activity on the same
                // channel (if any) is none of this app's business.
                var order = await orders.TryGetAsync(ev.OrderId, ct);
                if (order is null)
                {
                    continue;
                }

                // Grant/notify the user LINE itself attributes the purchase to, not the
                // client-supplied value recorded at reserve time (see Program.cs's
                // /api/shop/reserve) — ev.UserId comes from LINE's own IAP webhook payload, so
                // it's the authoritative identity even if a caller supplied a bogus userId when
                // reserving. Log a mismatch since it's a signal the reserve request was spoofed.
                var userId = ev.UserId ?? order.UserId;
                if (ev.UserId is not null && ev.UserId != order.UserId)
                {
                    _logger.LogWarning(
                        "Order {OrderId} was reserved with userId {ReservedUserId} but LINE attributes it to {ActualUserId}; using the latter.",
                        order.OrderId, order.UserId, ev.UserId);
                }

                switch (ev.Type)
                {
                    case "purchaseComplete":
                        if (await inventory.GrantAsync(userId, order.OrderId, order.ProductId, ct))
                        {
                            _logger.LogInformation(
                                "Granted {ProductId} to {UserId} (order {OrderId}).",
                                order.ProductId, userId, order.OrderId);
                            await NotifyPurchaseAsync(userId, order.ProductId, miniApp, messaging, notifierTokens, ct);
                        }
                        break;
                    case "refundComplete":
                        await inventory.RevokeAsync(userId, order.OrderId, ct);
                        break;
                }
            }

            cursor = page?.NextCursor;
        } while (!string.IsNullOrEmpty(cursor));

        _watermarkEpochSeconds = now;
    }

    // Fires right after a successful GrantAsync to tell the user in chat. Chapter 8 implements the
    // real thing (prefer a branded service message, fall back to a plain push); it's stubbed here so
    // Chapter 7 compiles and the reconciliation loop is complete on its own.
    private Task NotifyPurchaseAsync(
        string userId,
        string productId,
        MiniAppClient miniApp,
        MessagingClient messaging,
        INotifierTokenStore notifierTokens,
        CancellationToken ct)
        => Task.CompletedTask;
}
```

## ポーリングループ

`ExecuteAsync` は `PeriodicTimer` で `PollSeconds` ごとに刻みます。ポーリングが失敗しても、例外を捕まえて
次のtickでまた試すだけ——webhookハンドラと同じ考え方
（エラーはログに残して処理は止めない）を、今度はバックグラウンドループに当てはめた形です。ちなみに、
`CompanionSettings.PollSeconds` を正の値にクランプしていたのも、実はここに効いてきます。
`PeriodicTimer` のコンストラクタはこの try/catch の *外側* にあり、非正の間隔を渡すと例外を投げて
ホストごと巻き込んで落としてしまうからです。設定ミス一つでアプリ全体が起動しない、という事態を
避けているわけですね。

## 1回のポーリング: 全ページを歩いてから、ウォーターマークを進める

`PollOnceAsync` の全体は上のファイルのとおりです。短いループですが、この中には触れておきたい設計判断が
いくつも詰まっています。

- **`IOrderStore` が知っている注文だけに手を出す。** 実は `MiniAppWebhookEvent` は最初から
  `UserId`/`ProductId` を持っているので、ユーザーを *解決する* だけなら `OrderStore` は要りません。
  では何のためにあるのか——ゲートです。このアプリが `reserve` 経由で *自ら* 始めた購入だけを付与し、
  同じチャネル上で起きている他のIAP活動には一切関与しない、という線引きをここで引いています。
- **付与も通知も `ev.UserId` で行い、reserve時の `order.UserId` は使わない。** これは正しさの修正
  （権威を持つのはwebhookペイロードのほう）であると同時に、第6章で残した「`userId` をそのまま
  信じる」という簡略化への、具体的な埋め合わせでもあります。たとえ reserve が偽装されていても、
  本来の購入者への付与を横取りすることはできない、というわけです。
- **そもそも冪等に作ってある。** `GrantAsync` は `OrderId` をキーにしていて、二度目以降は静かに
  no-op になります。だから窓の途中でプロセスが再起動して範囲が重なって再スキャンされても、二重付与
  は起こりません。「処理済みイベント」を別に覚えておく仕組みは要らないのです。
- **末尾に少しだけバッファを取る**（`TrailingBufferSeconds = 5`）。ちょうど *now* まで問い合わせて
  しまうと、たった今完了したのにLINE側でまだインデックスされていないイベントを取りこぼしかねません。
  数秒だけ重ねて見ておくコストはゼロ（Grant/Revoke は冪等ですから）、それでこの隙間が埋まります。
- **ウォーターマークは、全ページを歩き切って初めて前進させる。** イベントごとに進めてしまうと、
  ページの途中でループが中断されたときに、静かに取りこぼしが生まれる恐れがあるからです。
- **返金も同じ形で扱う。** `refundComplete` は同じ `OrderId` を辿ってアイテムを取り消します。

## 動かしてみる

チャネルアクセストークンが無い状態だと、サービスは「無効です」とログに残して、あとは何もしません。
どちらに転んでもアプリは健全なまま、というのがここでの狙いです:

```
info: LineCompanionBot.Services.PurchaseReconciliationService[0]
      LINE_CHANNEL_ACCESS_TOKEN is not set — purchase reconciliation is disabled.
```

user-secrets にプレースホルダのトークンを入れてF5してみると、サービスは第2章・第4章でも見た
あの同じネットワーク境界まで到達します。外向きの通信ができなければポーリングは失敗し、ログを残して、
次のtickでまた試す——設計どおりの振る舞いです。とはいえ、本物の `purchaseComplete` を実際に拾える
かどうかは、実チャネルと完了済みの購入がそろって初めて確かめられます（[第9章](09-end-to-end.md)）。
付与のタイミングで発火する通知そのものは、次の章で配線していきましょう。
