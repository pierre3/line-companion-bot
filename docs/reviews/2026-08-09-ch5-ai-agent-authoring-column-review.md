# レビュー記録 — 第5章「AIエージェントに定義と画像を作らせる」番外編コラム追記

- **日付:** 2026-08-09
- **対象差分:** docs-only。`docs/manual/en/05-rich-menu.md` / `docs/manual/ja/05-rich-menu.md` に
  番外編コラム（任意）を追記。MCP 節の直後・「試してみる」の前。コード/テスト変更なし。
- **内容:** AIエージェント（Claude Code）に `richmenu.json` / `richmenu.png` を作らせ、`line` MCP + CLI で
  登録させる一気通貫フローの例プロンプト3つ（① 定義生成 / ② プレースホルダ画像生成 / ③ MCP+CLI 登録）。
  役割分担（作るのはエージェント、登録するのは MCP、画像アップロードは CLI 専用）を明記。

## 3役ゲート結果（サブエージェント）

| 役 | 判定 | 指摘 |
| --- | --- | --- |
| code-reviewer | PASS | 低1件: プロンプト①に必須プロパティ `name` の指示が抜け（実 `richmenu.json` は保持）。第9章の `create` で初めて失敗が顕在化しうる。 |
| security-reviewer | PASS | 指摘なし。トークン衛生・プレースホルダ・オフライン前提いずれも既存章と整合。 |
| test-arch-reviewer | PASS | 低2件: (a) 冒頭「MCP を繋いだなら」が①②までMCP前提に読める（実際は③のみMCP要）、(b) ③のコマンド順が本文の確立順 create→image→set-default と逆で、字面どおりだと画像未アップロードでデフォルト化しかねない。 |

## 対応（すべて反映済み）

- **code #1:** プロンプト①に `name` = `"LineCompanionBot default menu"` を追記（en/ja）。生成物が本文掲載の
  `richmenu.json` と完全一致するようにした。
- **test-arch (a):** 冒頭を「エージェント（Claude Code）があるなら」に変更し、①②がMCP不要である自説と一致させた（en/ja）。
- **test-arch (b):** ③プロンプトを create → image(CLI) → set-default の順に書き換え、末尾に順序を明記（en/ja）。

## 人の go/no-go

- 全役 PASS、低重大度の指摘は全件反映済み。ユーザー承認によりコミット。
