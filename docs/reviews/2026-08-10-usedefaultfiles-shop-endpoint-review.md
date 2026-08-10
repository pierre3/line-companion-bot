# レビュー記録 — UseDefaultFiles 追加で MINI App エンドポイントURLを /shop/ で指せるように

- **日付:** 2026-08-10
- **対象差分:** コード1点＋ドキュメント2点。
  - `src/LineCompanionBot/Program.cs`: `app.UseExceptionHandler();` の後・`app.UseStaticFiles();` の前に
    `app.UseDefaultFiles();` を追加（+コメント4行）。
  - `docs/manual/{en,ja}/06-shop.md`: Program.cs 配線説明を「`UseDefaultFiles` → `UseStaticFiles` の順／
    `/shop/` が `/shop/index.html` に解決」に更新。
- **背景:** 第9章のE2E検証中、MINI App チャンネルのエンドポイントURLに何を入れるかの質問から、アプリが
  `UseStaticFiles` のみで既定ドキュメント解決が無く `/shop/`（ディレクトリ）が 404、`/shop/index.html`
  のみ 200 だったことが判明。第9章トラブルシュートは `/shop/` パス前提で書かれており実挙動と齟齬。
  人の選択（案B）により、`/shop/` でも index.html を返すようアプリ側を修正し、マニュアルの `/shop/`
  表記を正とする方針に確定。

## 検証（実施済み）

- `dotnet build` 成功（0警告/0エラー）。
- 別ポート5099で実起動し確認: `GET /shop/`→200 text/html（本文 `<!DOCTYPE html>`）、
  `GET /shop/index.html`→200、`GET /`（health）→200、`GET /api/shop/catalog`→200。

## 3役ゲート結果（サブエージェント）

| 役 | 判定 | 指摘 |
| --- | --- | --- |
| code-reviewer | PASS | 指摘なし。配置順（UseStaticFiles の前）正しく ASP.NET 標準パターン。`wwwroot` 直下に index.html 無しで `/` ヘルスは非干渉。ドキュメント整合・英語コメント規約準拠。 |
| security-reviewer | PASS | `UseDefaultFiles` はURLリライトのみでディレクトリ一覧を有効化しない。配信範囲不変（`wwwroot/shop/*`）、`/` 情報開示不変、認可境界・トークン扱いに影響なし。 |
| test-arch-reviewer | PASS | ミドルウェア順が標準構成。むしろ Web 標準挙動に寄せる変更で CLAUDE.md 規約に合致。第6章/第9章の `/shop/` 表記が整合し従来の齟齬が解消。テスト追加不要（フレームワーク標準挙動）。低: コメントが現 wwwroot 構成に依存（既に明記済みで対応不要）。 |

## 人の go/no-go

- 全役 PASS。ビルド・実挙動とも確認済み。低nitはコメントが既にカバー。ユーザー承認（案B）により
  コミット＆push。
