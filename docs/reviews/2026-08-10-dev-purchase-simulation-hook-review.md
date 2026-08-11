# レビュー記録 — Development 限定の購入シミュレーションフック

- **日付:** 2026-08-10
- **対象差分:** コード＋フロント＋ドキュメント（テスト追加なし）。
  - `src/LineCompanionBot/Endpoints/ShopEndpoints.cs`（`isDev = app.Environment.IsDevelopment()` を
    単一真実源に、`/config` の `devPurchaseEnabled` と `POST /api/shop/dev/complete-purchase` を制御。
    後者は `isDev` ガード内でのみ Map。`DevCompletePurchaseRequest` レコード追加。付与は
    `dev-<Guid>` 起点で `GrantAsync`、成功かつ `MessagingClient` 有時のみ同一 push を送出、
    `{orderId, granted, notified}` を返す）
  - `src/LineCompanionBot/wwwroot/shop/shop.js`（`config.devPurchaseEnabled` 真時のみ
    「Mark purchased (dev)」ボタンを描画、`devComplete()` で `/dev/complete-purchase` を叩き
    `notified` に応じて文言分岐。IAP 不可時のステータス文言も分岐）
  - `docs/manual/{en,ja}/09-end-to-end.md`（「Development-only: simulate a purchase」節を追加。
    完全な IAP E2E が不可な理由、Buy 無効／403 が正常であること、下流検証の手順、
    無認可フックの注意書きコールアウトを併記）

## 背景（なぜ dev フックが要るか）

完全な IAP 購入 E2E は不可（LINE MINI App の IAP は利用申請＋約2週間審査＋認証審査＋日本＋事業者＋
手数料が前提。テスト決済も承認後・Developing チャネル・テスター限定）。承認前は Buy 無効
（`isApiAvailable('iap')` false）＋照合ポーリングが `/iap/v1/webhook/events` で 403＝正常挙動。
そこで**その先の下流（付与→通知→Feed での Golden Kibble 消費）だけ**を実機で検証するため、
Development 限定で「購入完了」を代替するフックを追加した。LINE の IAP エンドポイントには一切触れない
ため `isApiAvailable('iap')` が false でも動く。

## 検証

- `dotnet build` 成功（0 警告 / 0 エラー）、`dotnet test` 31件全通過。
- 参照リポの自己検証: Development で `{orderId, granted:true, notified:false}`（トークン無しのため
  notified false）、Production（`--no-launch-profile`）で dev エンドポイント 404・
  `devPurchaseEnabled:false`。
- **実チャネル・スマホ実機で E2E をユーザーが確認**: 「Mark purchased (dev)」→ push 受信 →
  在庫反映 → Feed で Golden Kibble 消費、まで成功（＝真の下流 E2E 検証）。

## 3役ゲート結果（サブエージェント）

| 役 | 判定 | 指摘 |
| --- | --- | --- |
| code-reviewer | PASS | `isDev` 単一真実源で `/config` とエンドポイントを一貫制御・ファサード利用/DI 妥当・push 失敗を握って握り潰す（付与は成立）設計・英語コメント準拠。 |
| security-reviewer | CONCERNS → 対応済み | **無認可の特権操作**（任意 `userId` へ付与＋push 可能）。ただし Production 非存在（`isDev` ガード）。ループバック限定/共有秘密での堅牢化は「スマホ→匿名トンネル」本来フローを壊すため不採用。代替として**第9章に注意書きコールアウト**（`--allow-anonymous` トンネル公開中は到達可能・短命運用・URL 非共有・テスト後閉じる）を追記して対応。 |
| test-arch-reviewer | PASS | Development 限定フックにつき自動テストは過剰（Minimal API/環境依存）・記録のみで妥当。付与が `OrderId` 起点で既存 `GrantAsync` の冪等性と整合。`dev-` prefix で実 OrderId と非衝突・毎クリック別注文で在庫が積み上がり消費テスト向き。 |

## 対応（反映済み）

- security CONCERNS（無認可の特権操作）→ 技術的封じ込め（ループバック/秘密）は本来フローを壊すため
  不採用の判断を人と確認のうえ、第9章 en/ja に注意書きコールアウトで運用リスクを明示して対応。
- 残差なし（テスト追加不要の判断は test-arch と一致）。

## 人の go/no-go

- code/test-arch PASS、security CONCERNS は第9章コールアウトで対応済み。実チャネル・実機で下流 E2E の
  成功をユーザーが確認済み。ユーザーの事前承認（「OK ならコミット Push」）により commit ＆ push。
