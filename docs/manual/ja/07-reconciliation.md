[← 第6章](06-shop.md) | [索引](README.md) | [第8章 →](08-notify.md)

# 第7章 — 購入照合

**このステップで作るもの:** `PurchaseReconciliationService` です。完了した購入を検知して、対応する
アイテムをユーザーに付与する `BackgroundService` で、第6章の `reserve` 呼び出しで開いたループを、
ここでようやく閉じます。

正直に言うと、ここはこのシステムでいちばん「ぎこちない」部分かもしれません。`MiniAppClient` には
IAPイベント用のpush webhookが存在しないからです（第2章のMessaging webhookとは対照的です）。用意
されているのは `GetWebhookEventsAsync`、つまり7日窓・カーソルページングの*pull* API だけです。
だから「完了した瞬間に受け取る」わけにはいかず、こちらから定期的に「前回チェック以降、何が起きたか」
と問い合わせに行きます。`LINE_MINIAPP_POLL_SECONDS`（デフォルト30秒）のタイマーで刻む、いわゆる
ポーリングです。

## 登録の仕方と、ライフタイムの機微

まずは `Program.cs` に一行:

```csharp
builder.Services.AddHostedService<PurchaseReconciliationService>();
```

ここで少し立ち止まりたいのがライフタイムの話です。`BackgroundService` はプロセスが生きている間
ずっと存在する **Singleton** です。一方で `InMemory*` ストアが今日 Singleton なのは、実は *たまたま*
にすぎません。将来これをRDBバックのストアに差し替えたら、そちらは通常 **Scoped**（作業単位
ごとに `DbContext` を1つ）になります。そして Singleton は Scoped 依存をそのまま抱え込めません。
最初に生成された `DbContext` を永久に握りしめてしまう、「captive dependency（捕捉された依存）」問題
です。

そこでこのサービスは、ストアを直接コンストラクタで受け取るのではなく `IServiceScopeFactory` を
受け取り、ポーリング1回ごとに新しいスコープからすべてを解決します。こうしておけば、あとから
ストアのライフタイムを変えても、このクラスには一切手を入れずに済みます。第3章でわざわざ永続化の
シーム（seam）を設けておいたのは、まさにこの瞬間のためでした。

## 完全なファイル

`src/LineCompanionBot/Services/PurchaseReconciliationService.cs` の全体です。以降の2節で
`ExecuteAsync` と `PollOnceAsync` を順に読み解きます。`NotifyPurchaseAsync`（付与直後の通知）は、
サービスが単体でビルド・完結できるよう、ここではスタブにとどめてあります。その中身（と、そこで必要に
なる using 2本）は第8章で実装します:

```csharp
using Line.OpenApi.Messaging;
using Line.OpenApi.MiniApp;
using LineCompanionBot.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LineCompanionBot.Services;

// IAP イベント用の push webhook は無いので、GetWebhookEventsAsync をポーリングする。設計上、冪等。
// IInventoryStore.Grant/Revoke は OrderId をキーにするので、再起動後に重なった窓を再スキャンしても
// 二重付与や二重取り消しは起こらない。
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
        // ストアはコンストラクタで直接受け取るのではなく、ポーリングごとに新しい DI スコープから
        // 解決する（PollOnceAsync 参照）。この BackgroundService はプロセスの生存期間ずっと Singleton
        // だが、I*Store 実装が今日 Singleton なのは（インメモリなので）たまたまにすぎない。将来の
        // RDB バックのストアは通常 Scoped（リクエスト/作業単位ごとの DbContext）になり、Singleton は
        // Scoped 依存を直接は持てない（「captive dependency」問題）。ここでスコープ経由で解決して
        // おけば、その差し替えでこのクラスに変更は要らない。
        _scopeFactory = scopeFactory;
        _logger = logger;
        // ポーリング対象はこの時点以降の購入だけ。まっさらなデモプロセスが、再起動のたびに
        // 7日分の履歴をすべて再スキャンする理由はない。
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
                // ポーリングの失敗でループを止めてはならない。次の tick でリトライするだけ。
                _logger.LogWarning(ex, "Purchase reconciliation poll failed; will retry next tick.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // 末尾の安全マージン。現在時刻ぎりぎりまで問い合わせると、少し前に完了したのに LINE 側でまだ
    // インデックスされていないイベントを取りこぼす恐れがある。数秒の重なりはコストゼロ
    //（Grant/Revoke は OrderId で冪等）で、その隙間を埋める。
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

        // ウォーターマークは、全ページを歩き切って完全に成功したときだけ進める。イベントごとに
        // 進めると、途中でループが中断されたときにページの残りを黙って飛ばす恐れがある。
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

                // このアプリ自身が予約した注文だけに反応する。同じチャネル上の他の IAP 活動が
                // あっても、このアプリの関知するところではない。
                var order = await orders.TryGetAsync(ev.OrderId, ct);
                if (order is null)
                {
                    continue;
                }

                // 付与・通知は、予約時にクライアントが渡した値ではなく、LINE 自身が購入を帰属させた
                // ユーザーに対して行う（Program.cs の /api/shop/reserve を参照）。ev.UserId は LINE 自身の
                // IAP webhook ペイロード由来なので、予約時に呼び出し元が偽の userId を渡していても、
                // これが正当な識別子。不一致は予約リクエスト偽装のサインなのでログに残す。
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

    // GrantAsync 成功の直後に発火し、チャットでユーザーに知らせる。実装は第8章（ブランド付きの
    // サービスメッセージを優先し、ダメなら素の push にフォールバック）。ここでは第7章が単体で
    // コンパイルでき、照合ループが完結するようスタブにしてある。
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
次のtickでまた試すだけです。webhookハンドラと同じ考え方（エラーはログに残して処理は止めない）を、
今度はバックグラウンドループに当てはめた形です。`CompanionSettings.PollSeconds` を正の値にクランプ
していたのも、ここに効いてきます。`PeriodicTimer` のコンストラクタはこの try/catch の *外側* にあり、
非正の間隔を渡すと例外を投げてホストごと巻き込んで落としてしまうからです。設定ミス一つでアプリ全体が
起動しない、という事態を避けています。

## 1回のポーリング: 全ページを歩いてから、ウォーターマークを進める

`PollOnceAsync` の全体は上のファイルのとおりです。短いループですが、この中には触れておきたい設計判断が
いくつも詰まっています。

- **`IOrderStore` が知っている注文だけに手を出す。** 実は `MiniAppWebhookEvent` は最初から
  `UserId`/`ProductId` を持っているので、ユーザーを *解決する* だけなら `OrderStore` は要りません。
  では何のためにあるのか。ゲートです。このアプリが `reserve` 経由で *自ら* 始めた購入だけを付与し、
  同じチャネル上で起きている他のIAP活動には一切関与しない、という線引きをここで引いています。
- **付与も通知も `ev.UserId` で行い、reserve時の `order.UserId` は使わない。** これは正しさの修正
  （権威を持つのはwebhookペイロードのほう）であると同時に、第6章で残した「`userId` をそのまま
  信じる」という簡略化への、具体的な埋め合わせでもあります。たとえ reserve が偽装されていても、
  本来の購入者への付与を横取りすることはできません。
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
どちらに転んでもアプリは健全なまま、というのが狙いです:

```
info: LineCompanionBot.Services.PurchaseReconciliationService[0]
      LINE_CHANNEL_ACCESS_TOKEN is not set — purchase reconciliation is disabled.
```

user-secrets にプレースホルダのトークンを入れてF5してみると、サービスは第2章・第4章でも見た
同じネットワーク境界まで到達します。外向きの通信ができなければポーリングは失敗し、ログを残して、
次のtickでまた試します。設計どおりの振る舞いです。ただし、本物の `purchaseComplete` を実際に拾える
かどうかは、実チャネルと完了済みの購入がそろって初めて確かめられます（[第9章](09-end-to-end.md)）。
付与のタイミングで発火する通知そのものは、次の章で組み込みます。
