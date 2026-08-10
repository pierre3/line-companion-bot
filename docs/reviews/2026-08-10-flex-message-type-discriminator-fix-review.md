# レビュー記録 — Flex/メッセージ POCO の type 判別子欠落による 400 拒否の修正

- **日付:** 2026-08-10
- **対象差分:** コード＋テスト＋ドキュメント。
  - `src/LineCompanionBot/Services/PetFlexMessageFactory.cs`（`BuildStatus`/`BuildPlayRefused` の全
    POCO に `Type` を設定、クラスコメント追記）
  - `src/LineCompanionBot/Services/PurchaseReconciliationService.cs`（push の `TextMessage` に
    `Type = "text"`）
  - `tests/LineCompanionBot.Tests/FlexMessageDiscriminatorTests.cs`（新規・回帰テスト）
  - `docs/manual/{en,ja}/02-webhook.md`（echo `TextMessage` に `Type`＋判別子の説明を「繰り返す
    ポイント」に追加）、`04-flex-postback.md`（Flex ファクトリの各ノードに `Type`＋「落とし穴」
    コールアウト）、`08-notify.md`（push `TextMessage` に `Type`）

## 背景（実チャネル E2E で発見した実バグ）

第9章の実チャネル検証で、リッチメニューの FEED/PLAY/STATUS の Flex 返信が `Reply.PostAsync` で
`400 "The request body has 1 error(s)"` になり無反応だった。原因は、`Line.OpenApi.Messaging` の生成
POCO（`FlexMessage`/`FlexBubble`/`FlexBox`/`FlexText`、`TextMessage`）が `type` 判別子をデフォルト値
無しの `Type` プロパティで持ち、`Serialize` が非null時のみ書き出す設計のため、手組みで `Type` 未設定
だと `"type"` が JSON から欠落し LINE に弾かれていたこと。**オフライン先行で Flex を実送信したことが
無かった**ため、この盲点が実 E2E で初めて表面化した。

## 検証

- `dotnet build` 成功、`dotnet test` 31件全通過（新規回帰テスト2件含む）。
- **実チャネルで FEED/PLAY/STATUS の Flex 返信が成功することをユーザーが確認**（＝真の E2E 検証）。

## 3役ゲート結果（サブエージェント）

| 役 | 判定 | 指摘 |
| --- | --- | --- |
| code-reviewer | PASS | 判別子の値・網羅性（src 全 grep で漏れなし）・回帰テスト妥当・英語コメント準拠。低: en Ch4 の blockquote 前の空行欠落（ja と不揃い）→ 修正済み。info: マニュアルのクラスコメント省略（意図的・据え置き）。 |
| security-reviewer | PASS | `type` 定数付与のみ。トークン/シークレット・ログ・認可境界に変化なし。本文への機微情報混入なし。テストにシークレットのハードコードなし。 |
| test-arch-reviewer | PASS | 回帰テストがバグを的確にガード・過不足なし。既存スタイル準拠。低: `PurchaseReconciliationService` の push は未ガード（private/BackgroundService でテストは過剰、記録のみ）。低(doc): Ch2/Ch8 の `TextMessage.Type` 追加が無言 → Ch2 に説明を追記して解消。 |

## 対応（すべて反映済み）

- code 低（en Ch4 空行）→ 空行追加で en/ja 整合。
- test-arch 低(doc)（`TextMessage.Type` の無言追加）→ Ch2 の「繰り返すポイント」に判別子の説明を en/ja で追加。
- 残る低/info（push のテスト過剰、クラスコメント省略）は記録のみ・対応不要。

## 人の go/no-go

- 全役 PASS、低指摘は反映済み。実チャネルで FEED/PLAY/STATUS の返信成功をユーザーが確認済み。
  ユーザーの事前承認（「OKならコミットPush」）によりコミット＆push。
