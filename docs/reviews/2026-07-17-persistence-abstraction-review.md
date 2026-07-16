# 2026-07-17 — 永続化抽象化リファクタリング レビュー（3役）

前回コミット（`f2fa19d`）後、ユーザー要望により実施したリファクタリングの3役ゲート。対象:
`PetStore`/`OrderStore`/`InventoryStore`/`NotifierTokenStore`を`I*Store`インターフェース
（`src/LineCompanionBot/Persistence/`）越しの公開に変更し、`AddInMemoryPersistence()`1箇所の
DI登録差し替えで将来RDB実装へ切り替え可能な作りにした。`PurchaseReconciliationService`は
ストアをコンストラクタ依存から`IServiceScopeFactory`経由のスコープ単位解決へ変更。

## 判定

| 役 | 判定 |
|---|---|
| コードレビュアー | CONCERNS（非ブロッキング） |
| セキュリティレビュアー | CONCERNS（非ブロッキング） |
| テスト・アーキ観点 | CONCERNS（非ブロッキング） |

FAIL/BLOCKingな指摘なし。3役とも同一の主要指摘（下記#1）に収斂。実行可能な指摘はすべて反映済み。

## 指摘と対応

| # | 出所 | 指摘 | 重大度 | 対応 |
|---|---|---|---|---|
| 1 | コード / セキュリティ / テスト・アーキ（3役一致） | 本リファクタリングがCLAUDE.mdの既存確定方針（「永続化層は追加しない」「呼び出し元1箇所の抽象化は作らない」）と矛盾しており、CLAUDE.md側が更新されていない | High | **修正**: ユーザーへ方針転換の可否を確認（AskUserQuestion）、「CLAUDE.mdを更新して採用を確定」を選択。CLAUDE.mdの該当2箇所を、この抽象化を明示的な例外として採用した旨に更新（2026-07-17付） |
| 2 | セキュリティ | `/api/shop/reserve`で、外部（LINE）側は既にコミット済み（`ReserveProductAsync`成功後）の`orderStore.RecordAsync`にリクエストの`CancellationToken`をそのまま使っており、将来キャンセルを尊重する実ストアに差し替えた際、クライアント切断で発注記録だけが黙って失われうる | Medium | **修正**: 当該呼び出しを`CancellationToken.None`に変更、理由をコメントで明記 |
| 3 | セキュリティ | `/webhook`のfeedアクションで、`inventory.TryConsumeAsync`（希少アイテム消費）と`petStore.SaveAsync`（その効果の保存）が同一の`ct`を使っており、将来実ストアでキャンセルが間に割り込むとアイテムだけ消費されて効果が保存されない不整合が起きうる | Medium | **修正**: 両呼び出しを`CancellationToken.None`に変更、理由をコメントで明記 |
| 4 | テスト・アーキ | 新規`Persistence/`層（`I*Store`とその実装、DI登録）にテストが皆無 | Medium | **修正**: `InMemoryInventoryStoreTests`（Grant冪等性、Revoke/TryConsumeの正常系・異常系、計5件）と`PersistenceServiceCollectionExtensionsTests`（`AddInMemoryPersistence()`が4インターフェースをSingletonとして解決することを検証、1件）を追加 |
| 5 | コード | `PurchaseReconciliationService`のスコープ単位解決は、現状ストアが全てSingletonのため今は不要なオーバーヘッド | Info | **対応不要**: #1でCLAUDE.md側を更新し抽象化自体を正式採用したため、その前提となるcaptive dependency対策も設計通り維持 |
| 6 | コード | `NotifierTokenStore.Remove(string)`が新インターフェースに引き継がれていない | Info | **対応不要**: リファクタリング前から呼び出し元ゼロのデッドコードであることを確認済み（削除は正しい判断） |

## 検証

- `dotnet build`: 0警告・0エラー
- `dotnet test`: 24/24 合格（リファクタリング前18/18、テスト追加で24に）
- 手動スモークテスト: `/`・`/api/shop/catalog`・`/api/shop/inventory/{userId}`（新非同期パス）・
  `/webhook`（未設定時503）・`/api/shop/reserve`（未設定時503）を実行中のインスタンスに対して確認

## 記録

各役の詳細な指摘全文はこのセッションのサブエージェント出力として生成され、本ファイルには
反映結果のみを要約している（生の指摘全文は保存していない——前回レビューと同じ慣行）。

## Go/No-Go

**GO推奨。人の最終go/no-go待ち（未コミット）。**
