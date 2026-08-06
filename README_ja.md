[English](README.md) | **日本語**

# LineCompanionBot

[`Line.OpenApi.*`](https://github.com/pierre3/line-openapi-dotnet) .NET クライアントライブラリ群を
一気通貫で使いこなす統合サンプルとして作った、バーチャル相棒育成LINE bot × MINI Appショップです。
ユーザーはLINEチャットでリッチメニューから相棒の世話をし（postback→Flex返信）、MINI Appショップで
レア餌・スキンをIAP課金購入すると、購入完了がチャットへ通知されます。

このアプリをどう組み立てたか——`dotnet new` から一気通貫の動作確認まで、実装ステップごとに1章——を
追体験できるハンズオンマニュアルが [`docs/manual/ja/`](docs/manual/ja/README.md) にあります。

## 必要環境

- .NET 10 SDK
- LINE Messaging APIチャンネル（Bot用）と LINE MINI Appチャンネル（ショップ用）
  ——コンソール設定手順はチュートリアル参照。

## 環境変数

| 変数 | 用途 |
|---|---|
| `LINE_CHANNEL_SECRET` | Webhook署名検証 |
| `LINE_CHANNEL_ACCESS_TOKEN` | Reply/Push/RichMenu/Notifier/IAPポーリング用チャネルトークン |
| `LINE_MINIAPP_LIFF_ID` | リッチメニューのショップボタンURIAction用LIFF ID |
| `LINE_MINIAPP_TEMPLATE_NAME` | 審査済みサービスメッセージテンプレート名（未設定ならPushのみ） |
| `LINE_MINIAPP_POLL_SECONDS` | 購入照合のポーリング間隔（既定30） |

## 実行方法

シークレットは開発時 `dotnet user-secrets` から読み込みます（環境変数でも可）。チュートリアルは
Visual Studio Code（コミット済みの `.vscode/` 設定でF5起動/デバッグ）を前提にしていますが、CLIでも
動きます:

```powershell
dotnet user-secrets set LINE_CHANNEL_SECRET       "<チャネルシークレット>"       --project src/LineCompanionBot
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "<チャネルアクセストークン>" --project src/LineCompanionBot

# 初回のみ: リッチメニューを作成しデフォルト設定
dotnet run --project src/LineCompanionBot -- setup

# アプリ起動
dotnet run --project src/LineCompanionBot
```

VS Codeのセットアップ、dev tunnelでのWebhook公開手順、MINI AppショップのLINE Developers Console
設定を含む詳細は [`docs/manual/ja/`](docs/manual/ja/README.md) を参照してください。

## ビルド・テスト

```powershell
dotnet build
dotnet test
```

## 既知の制約

- `POST /api/shop/reserve`は、自身の帳簿付け（`OrderStore`）のために、クライアントが送ってきた
  `userId`をそのまま信頼します（`Line.OpenApi.MiniApp`はLIFFアクセストークンからサーバ側で検証
  する呼び出しを提供していません）。ただし肝心な箇所では緩和されています:
  `PurchaseReconciliationService`はLINE自身のIAP webhookペイロードが購入を帰属させる`userId`で
  付与・通知を行い、クライアントが送ってきた値は使いません——つまり呼び出し元が実際の購入の
  付与・通知先を別のLINEユーザーへ差し替えることはできません。`GET /api/shop/inventory/{userId}`
  にも本人確認はありません——認証層を一切持たないデモとしては妥当です（LINEの`userId`自体は
  意味のある秘匿情報ではないため）。
- `ReserveProductAsync`の`clientIp`導出に使う`X-Forwarded-For`ヘッダは、信頼できるプロキシの
  許可リストに対して検証されていないため、直接の呼び出し元が任意の値を設定できます。検証済みの
  クライアントIPではなく、ベストエフォートの不正利用対策シグナルとして扱ってください。
- クライアント側の`liff.iap.createPayment()`が`reserve`成功後にキャンセル/失敗した場合、
  `IOrderStore`のエントリとLINE側の予約注文は解放されないままになります——`Line.OpenApi.MiniApp`
  に予約解放APIが無いためです。実害はありません（`PurchaseReconciliationService`は実際に
  `purchaseComplete`まで到達した`OrderId`にしか反応しないため）が、インメモリストアには
  使われないレコードが残り続けます。

## ステータス

[`docs/manual/ja/`](docs/manual/ja/README.md) の全9章に沿って機能完成、さらに3役レビュー
（code/security/test-arch、いずれもCONCERNS非ブロッキング）を実施し指摘を反映済み——レビューの
指摘は末尾に別節としてまとめず、該当する各章に畳み込んでいます。実LINEチャネル無しでローカル確認済み:
署名検証、postback→Flex
応答分岐、`setup`のCLIコマンド、ショップの全エンドポイント、購入照合のポーリング/リトライループが
`api.line.me`へ到達すること。エンドツーエンドの完全な動作（チャット返信・リッチメニュー表示・
実際のIAP購入完了）には実Messaging API + MINI Appチャネルの接続が必要です——チュートリアル
第9章参照。
