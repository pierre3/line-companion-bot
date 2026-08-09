# レビュー記録 — リッチメニュー登録を外部ツール `Line.OpenApi.Tools` へ移行

- **日付:** 2026-08-09
- **対象変更:** Bot アプリ内蔵の `dotnet run -- setup` verb（`Services/RichMenuBootstrapper.cs`）を撤去し、
  リッチメニュー登録を外部 CLI/MCP ツール `Line.OpenApi.Tools`（`line richmenu create/image/set-default`）へ
  移行。定義は静的 `src/LineCompanionBot/assets/richmenu.json`。マニュアル第5章の全面書き換え、第1/9/0章・
  各 README・CLAUDE.md・`security-reviewer.md` のクロス参照更新を含む。
- **ゲート:** 3役サブエージェント（code / security / test-arch）を並列実行。
- **ビルド/テスト:** `dotnet build` 0エラー、`dotnet test` 29/29 成功。

## 判定サマリ

| 役 | 判定 | 要点 |
|---|---|---|
| code-reviewer | CONCERNS → 対応済み | 実指摘1件（Program.cs の stale コメント）。richmenu.json の形状・座標・postback 一致は確認済み。 |
| security-reviewer | PASS | 管理面は縮小（Bot は `/`・`/webhook`・`/api/shop/*` のみ）。秘密の混入なし。署名検証/DI/認可は不変。 |
| test-arch-reviewer | PASS（軽微CONCERNS） | 孤児テストなし。オフライン検証の物語は第5→9章で整合。stale コメント＋nice-to-have 2件。 |

## 指摘と対応

1. **[code / test-arch, Low] `Program.cs` の user-secrets コメントが削除済み「setup 経路」を参照**
   → **修正済み。**「both the web host and the "setup" path below」→「the web host」に簡素化。

2. **[security, Low] トークンの取り扱い注意（env/シェル履歴・`~/.line/config.json` 平文）**
   → **対応済み。** 第5章「登録する」に「失効させられるトークンを使い、用が済んだら消す」旨を追記（en/ja）。

3. **[test-arch, nice-to-have] `richmenu.json` の postback `data` と `WebhookEndpoints` の switch は手動同期**
   → **対応済み。** 第5章に「片方の名前を変えたら両方変える。さもないとタップ無反応」を追記（en/ja）。

4. **[test-arch, nice-to-have] `YOUR_LIFF_ID` 未置換のまま登録すると Shop ボタンが壊れる（旧実装の安全既定の喪失）**
   → **対応済み。** 第9章トラブルシュート「Shopボタンが空白」に、未置換ケースと再登録手順を追記（en/ja）。

5. **[code, Info] `richmenu.json` の shop URI はプレースホルダで、置換前でも `create` は通る**
   → 第5章・第9章とも「作成前に `YOUR_LIFF_ID` を置換」と明示済み。追加対応不要。

## 結論

ブロッカーなし。実指摘（stale コメント）と軽微な nice-to-have はすべて反映済み。人の go/no-go 待ち。
