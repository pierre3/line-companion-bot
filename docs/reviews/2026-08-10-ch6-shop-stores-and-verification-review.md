# レビュー記録 — 第6章 ストア型定義の補完とハンズオン検証由来の修正

- **日付:** 2026-08-10
- **対象差分:** docs-only。`docs/manual/en/06-shop.md` / `docs/manual/ja/06-shop.md`（+355/-4）。コード変更なし。
- **背景:** 第6章のハンズオン実地検証（作業フォルダはリポジトリ本体とは別）に入り、持ち越しの既知欠落
  （3ストアの型定義未掲載＝ビルド不可）と、検証中に見つかった実挙動の不一致を修正。

## 変更内容

1. **3ストアの型定義を完全ファイルで追加**（既知欠落の解消）— `IOrderStore`/`InMemoryOrderStore`、
   `IInventoryStore`/`InMemoryInventoryStore`、`INotifierTokenStore`/`InMemoryNotifierTokenStore`。
   実ファイル `src/LineCompanionBot/Persistence/**` から逐語コピー。第3章の `IPetStore`/`InMemoryPetStore`
   と同じ提示スタイル（インターフェース → インメモリ実装）。
2. **フロント3ファイルのコピー手順**（`index.html`/`shop.js`/`shop.css`）— 参照リポジトリ
   `wwwroot/shop/` からの `New-Item -ItemType Directory -Force` + `Copy-Item`。抜粋のままとする方針
   （A案・人の判断）を明示。静的アセットで `dotnet build` に影響せず、実ページは第9章。
3. **feed 分岐アップグレードの精度**— `IInventoryStore inventory` の追加位置（`IPetStore petStore` の次・
   `CancellationToken ct` の直前）と「第4章で追加済みの `using LineCompanionBot.Persistence;` に含まれる
   ため新しい using 不要」を明示（第4章の粒度に合わせる）。
4. **「動かしてみる」に `LINE_CHANNEL_ACCESS_TOKEN` 前提を追加**（実挙動の誤り修正）— `/reserve` は
   `!settings.HasMessaging` → 503 を最初に通る（`ShopEndpoints.cs:33`）ため、トークン未設定だと try-it の
   2呼び出しが期待の 400/404 ではなく 503 を返していた。第4章と同一のダミー `demo-token` を設定する
   手順を明記。

## 3役ゲート結果（サブエージェント）

| 役 | 判定 | 指摘 |
| --- | --- | --- |
| code-reviewer | PASS | ブロッカー無し。6ブロックは実ファイルとバイト一致、引数位置・503短絡・Copy-Item・en/ja 一致を確認。フロント抜粋は意図的例外として記録のみ。 |
| security-reviewer | PASS | ブロッカー無し。`demo-token` は第4章と同一のダミー・投入先 user-secrets で整合。try-it は 400/404 でネットワーク前に短絡しトークン送出なし。notifier トークンのログ出力なし。 |
| test-arch-reviewer | PASS | 低3件（下記）。ストア抽象の冪等性・ロック・consume 安全性の説明が第7/8章の物語および設計方針と整合。オフライン先行維持。 |

## 低重大度の指摘と対応

- **test-arch #1**（「Register all three」ブロックが Pet 含む4行で一瞬紛らわしい）— 文言で新規3＋既存Pet は
  読めるため現状維持。
- **test-arch #2**（第3章プレースホルダの列挙順 `IInventoryStore, IOrderStore, INotifierTokenStore` が
  実コード/第6章の `Order → Inventory → Notifier` と表記ゆれ）— **第3章側**の純粋な表記差で機能影響なし・
  本バッチ対象外。任意フォローアップ候補として記録（本コミットには含めない）。
- **test-arch #3 / URL**（参照リポジトリ `github.com/pierre3/line-companion-bot` の公開状態）— 第5章で
  既出の同一URL。公開前チェック項目として記録。
- **security 補足**（`/api/shop/inventory/{userId}` が任意ユーザー在庫を参照可）— 既存コード仕様で本差分
  対象外。将来の RDB 実装時に認可境界を再検討する事項として記録。

## 人の go/no-go

- 全役 PASS、低重大度の指摘はいずれも本差分の修正不要（対象外／記録のみ）。ユーザー承認によりコミット＆push。
