# 2026-07-16 — LineCompanionBot 実装レビュー（3役）

初回実装（全9ステップ、`docs/manual/{en,ja}/tutorial.md`参照）完了後の3役ゲート。

## 判定

| 役 | 判定 |
|---|---|
| コードレビュアー | CONCERNS（非ブロッキング） |
| セキュリティレビュアー | CONCERNS（非ブロッキング） |
| テスト・アーキ観点 | CONCERNS（非ブロッキング） |

FAIL/BLOCKingな指摘なし。実行可能な指摘はすべて反映済み（下記）。

## 指摘と対応

| # | 出所 | 指摘 | 重大度 | 対応 |
|---|---|---|---|---|
| 1 | セキュリティ / テスト・アーキ | `/api/shop/reserve`がクライアント供給の`userId`を検証せず信頼 | Medium | **修正**: `PurchaseReconciliationService`が付与・通知に`ev.UserId`（LINE自身のIAP webhookペイロード）を使用するよう変更、`order.UserId`との不一致は警告ログ。実質的な影響を排除。加えてREADME/チュートリアルに既知の制約として明記 |
| 2 | コード / セキュリティ | `X-Forwarded-For`がプロキシ検証なしで信頼される | Low | **文書化のみ**（コード修正はスコープ外と判断）: `Program.cs`にコメント追加、README/チュートリアルに既知の制約として明記 |
| 3 | コード | "Golden Kibble"購入がゲームプレイに何の効果も持たない（カタログ説明と矛盾） | Medium | **修正**: `InventoryStore.TryConsume` + `PetGrowthEngine.FeedRare`を追加、feedハンドラで未消費のrare-foodを消費して満タン回復 |
| 4 | コード | サービスメッセージ送信成功後のトークン保存が同一try内にあり、二重push送信のリスク | Low-Medium | **修正**: `NotifyPurchaseAsync`のtry/catchを分離、送信結果のみでフォールバック判定 |
| 5 | テスト・アーキ | ポーリング窓が現在時刻ぎりぎりまでで、直近イベントの取りこぼしリスク | Low | **修正**: `TrailingBufferSeconds = 5`のバッファを追加 |
| 6 | テスト・アーキ | `InventoryStore.Get`が`Grant`/`Revoke`と同期していない（並行アクセス時のレース） | Low-Medium | **修正**: 同一ロックの下でスナップショットを返すよう変更 |
| 7 | テスト・アーキ | `Play`のHappiness 100クランプが未テスト（チュートリアルは網羅済みと主張） | Medium | **修正**: テストケース追加（`Play_IncreasesHappiness_ClampedAt100`、`FeedRare_RefillsHungerToFull_RegardlessOfStartingValue`） |
| 8 | セキュリティ | 両方の通知経路（サービスメッセージ・push）が失敗した場合のログレベルがWarningのみ | Info | **修正**: 両方失敗時は`LogError`に格上げ |
| 9 | セキュリティ | `/api/shop/inventory/{userId}`に本人確認なし | Info | **対応不要**: 「認証層なし」という明示済みの設計方針の範囲内、影響も限定的（読み取りのみ、LINE userIdは秘匿情報ではない） |
| 10 | コード | `/api/shop/reserve`内でエラー応答形状が`Results.Problem`と素の文字列で不統一 | Low | **修正**: 全て`Results.Problem`に統一 |

## 検証

- `dotnet build`: 0警告・0エラー
- `dotnet test`: 18/18 合格（修正前16/16、テスト追加で18に）
- 修正後も`dotnet run -- setup`・Webhook署名検証・ショップエンドポイント・購入照合ポーリングの
  手動スモークテストは反実装中に実施済み（`docs/manual/en/tutorial.md`各章参照）

## 記録

各役の詳細な指摘全文はこのセッションのサブエージェント出力として生成され、本ファイルには
反映結果のみを要約している（生の指摘全文は保存していない——line-dotnet側の慣行と同じ）。

## Go/No-Go

**GO推奨。人の最終go/no-go待ち（未コミット）。**
