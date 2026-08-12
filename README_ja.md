[English](README.md) | **日本語**

# LineCompanionBot

[`Line.OpenApi.*`](https://github.com/pierre3/line-openapi-dotnet) .NET クライアントライブラリ群の
使い方を一通り示すサンプルアプリです。バーチャル相棒育成 LINE bot と MINI App ショップを組み合わせて
います。ユーザーはチャットのリッチメニューから相棒の世話をし（postback→Flex 返信）、MINI App
ショップでレア餌やスキンを IAP 課金で買うと、購入完了がチャットに通知されます。

このアプリの作り方を `dotnet new` から動作確認まで、実装ステップごとに1章ずつ解説したハンズオン
マニュアルを [`docs/manual/ja/`](docs/manual/ja/README.md) に用意しています。

## 必要環境

- .NET 10 SDK
- LINE Messaging API チャンネル（Bot 用）と LINE MINI App チャンネル（ショップ用）
  ——コンソールでの設定手順はチュートリアルを参照。

## 環境変数

| 変数 | 用途 |
|---|---|
| `LINE_CHANNEL_SECRET` | Webhook 署名検証 |
| `LINE_CHANNEL_ACCESS_TOKEN` | Reply/Push/RichMenu/Notifier/IAP ポーリング用チャネルトークン |
| `LINE_MINIAPP_LIFF_ID` | リッチメニューのショップボタン URIAction 用 LIFF ID |
| `LINE_MINIAPP_TEMPLATE_NAME` | 審査済みサービスメッセージテンプレート名（未設定なら Push のみ） |
| `LINE_MINIAPP_POLL_SECONDS` | 購入照合のポーリング間隔（既定 30） |

## 実行方法

シークレットは開発時 `dotnet user-secrets` から読み込みます（環境変数でも可）。チュートリアルは
Visual Studio Code（`.vscode/` 設定を同梱、F5 で起動/デバッグ）を前提にしていますが、CLI でも
動きます。

```powershell
dotnet user-secrets set LINE_CHANNEL_SECRET       "<チャネルシークレット>"       --project src/LineCompanionBot
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "<チャネルアクセストークン>" --project src/LineCompanionBot

# アプリ起動
dotnet run --project src/LineCompanionBot
```

リッチメニューはアプリではなく `Line.OpenApi.Tools` の CLI（`dotnet tool install -g
Line.OpenApi.Tools`）で一度だけ登録します——チュートリアル第5章を参照してください。

VS Code のセットアップ、dev tunnel での Webhook 公開、MINI App ショップの LINE Developers Console
設定など、詳しい手順は [`docs/manual/ja/`](docs/manual/ja/README.md) にあります。

## ビルド・テスト

```powershell
dotnet build
dotnet test
```

## 既知の制約

- `POST /api/shop/reserve` は、自身の記録（`OrderStore`）用にクライアントから送られてきた `userId`
  をそのまま信頼します（`Line.OpenApi.MiniApp` に LIFF アクセストークンをサーバ側で検証する API が
  ないため）。ただし肝心な部分は守られています。`PurchaseReconciliationService` は、付与と通知を
  LINE の IAP webhook が示す `userId` で行い、クライアントから送られてきた値は使いません。つまり
  呼び出し元が、実際の購入の付与・通知先を別の LINE ユーザーにすり替えることはできません。
  `GET /api/shop/inventory/{userId}` にも本人確認はありませんが、認証を持たないデモとしては許容範囲
  です（LINE の `userId` はそもそも秘匿すべき情報ではありません）。
- `ReserveProductAsync` の `clientIp` を求めるのに使う `X-Forwarded-For` ヘッダは、信頼できるプロキシ
  の許可リストと照合していないため、直接の呼び出し元が任意の値を入れられます。検証済みのクライアント
  IP ではなく、あくまで簡易的な不正対策の目安として扱ってください。
- クライアント側の `liff.iap.createPayment()` が `reserve` 成功後にキャンセル/失敗すると、
  `IOrderStore` のエントリと LINE 側の予約注文が解放されずに残ります（`Line.OpenApi.MiniApp` に予約
  解放 API がないため）。ただし実害はなく（`PurchaseReconciliationService` は `purchaseComplete` まで
  到達した `OrderId` にしか反応しません）、インメモリストアに使われないレコードが残るだけです。
