# CLAUDE.md — LineCompanionBot（バーチャル相棒育成Bot × MINI Appショップ）

このファイルは Claude Code が各セッションで自動読み込みするプロジェクト文脈です。

@docs/SESSION-HANDOFF.md

> 引継ぎの運用: セッション終了時に `/handoff` で `docs/SESSION-HANDOFF.md` に一時状態を保存し、
> 次セッションでこの import 経由で自動読み込みして再開する。内容を消化したら `/handoff-clear` で
> 空テンプレートへ戻す（手動クリア）。`docs/SESSION-HANDOFF.md` は Git 追跡対象外のローカル専用。

## プロジェクト概要

`Line.OpenApi.*`（`C:\Work\claude\line-dotnet` で開発している LINE 向け .NET クライアント
ライブラリ群）の"開発体験としての説得力"を示す統合サンプルアプリ。Bot（Messaging + Webhook +
RichMenu）と MINI App（サービスメッセージ通知 + IAP）を1つのユーザーフローで繋いだ**「バーチャル
相棒育成Bot」**——ユーザーはLINEチャットでリッチメニューから相棒の世話をし（postback→Flex返信）、
MINI Appショップでレア餌・スキンをIAP課金購入すると、購入完了がチャットへ通知される。

このリポジトリは `line-dotnet` とは独立したアプリケーションリポジトリで、`Line.OpenApi.*` を
**NuGetパッケージとして消費する側**（`ProjectReference`ではなく`PackageReference`）。開発方針・
レビュー体制・`.claude`設定は `line-dotnet` から移植し（ユーザー承認済み、2026-07-16）、同水準の
開発規律で進める。

**成果物は2つ、同時完成がゴール**: (1) 動くアプリ本体、(2) それを組み立てる手順を追体験できる
ハンズオンマニュアル（`docs/manual/{en,ja}/tutorial.md`、実装ステップと1:1対応、bilingual）。
実装ステップを完了する都度、対応する章を書く（まとめて後書きしない）。

## 確定している設計方針

- **TFM:** `net10.0` 単一（`Nullable=enable`）。消費先ライブラリの制約に合わせる。
- **参照パッケージ:** `Line.OpenApi.Messaging` / `Line.OpenApi.Messaging.Webhook` /
  `Line.OpenApi.MiniApp`（version `0.2.0-preview`）。`Line.OpenApi.Liff` は参照しない
  （呼ばない依存を足さない）。
- **状態管理:** デフォルト実装はすべてインメモリ（`ConcurrentDictionary`）。ただし
  `src/LineCompanionBot/Persistence/`配下の`I*Store`インターフェース（`IPetStore`/`IOrderStore`/
  `IInventoryStore`/`INotifierTokenStore`）越しに公開しており、`AddInMemoryPersistence()`
  （`Persistence/InMemory/PersistenceServiceCollectionExtensions.cs`）1箇所のDI登録を差し替える
  だけで将来RDB等の実装に切り替えられる作りにしている（2026-07-17、人の承認により方針変更・
  下記「過剰設計を避ける」規約の明示的な例外として採用）。インターフェースは実I/Oを見据えて
  async形（`Task`/`Task<T>`）にしてあるが、現行実装（`InMemory*`）は同期処理をラップしているだけ
  で実際には一度もawaitしない。
- **Pet状態:** Hunger/Happiness を参照時に遅延減衰計算（バックグラウンドタイマーは使わない）。
  `feed`/`play`/`status` の3アクション、レベルは `1 + Xp/50`（テーブル無し）。
  「死亡」等マイナス方向の状態は追加しない。
- **リッチメニュー:** `dotnet run -- setup` で一度だけブートストラップ（`RichMenuClient` 経由）。
  Web上に管理操作用エンドポイントは公開しない。
- **IAP完了検知:** push webhook は存在しない。`PurchaseReconciliationService`
  (`BackgroundService`) が `GetWebhookEventsAsync` を定期ポーリングして検知する。
  付与は `OrderId` 起点で冪等（プロセス再起動時のカーソル欠落を許容）。
- **通知フォールバック:** `SendServiceMessageAsync`（審査済みテンプレート + 有効な notifier
  トークンが揃う時のみ試行）→ どちらか欠けている、または例外時は必ず通常の `Push` メッセージへ
  フォールバックする。テンプレート未審査でもデモが動くことを優先。

## ライブラリ利用上の注意（`line-dotnet` の CLAUDE.md から消費者視点で抜粋）

- **`MessagingClient` の BaseUrl 設定順序:** data系ホスト（`api-data.line.me`）を使う操作
  （例: リッチメニュー画像アップロード）は、クライアント構築**前**に BaseUrl を設定する必要が
  ある設計になっている。ファサード（`RichMenuClient`等）を使う限りは内部で解決済みなので通常は
  意識不要だが、低レベル `MessagingClient.Api`/`.Blob` を直接叩く場合は要注意。
- **`Action` → `ActionObject`:** Kiota が `System.Action` との衝突回避で多態基底型を
  `ActionObject` にリネームしている。`PostbackAction`/`URIAction` 等の具象型はそのまま。
- **`MiniAppClient` のnotifier系エンドポイント:** `IssueNotificationTokenAsync`/
  `SendServiceMessageAsync` は **stateless/short-lived チャネルアクセストークンのみ受理**
  （長期トークンは拒否される）。`GetWebhookEventsAsync`/`ReserveProductAsync` はこの制約なし。
- **IAP完了の検知方法:** push webhook が無いため `GetWebhookEventsAsync` のポーリングのみ
  （7日窓・カーソルページング）。

## レビュー運用

`docs/REVIEW-WORKFLOW.md` 準拠。3 役（コード/セキュリティ/テスト・アーキ）を実装完了後のゲートとし、
サブエージェントで実行、**最終 go/no-go は人**。結果は `docs/reviews/` に日付付きで記録。
実装完了時点で必ず先にゲートへ回す（実装→コミット→マージを先行させない）。

- **レビュアーサブエージェント:** `.claude/agents/*.md` の3役（`code-reviewer` /
  `security-reviewer` / `test-arch-reviewer`）を Agent ツールの `subagent_type` で直接起動できる。

## 規約

- 全コメントは英語（XML doc・インライン共に）。公開しうるOSSサンプルとして一貫性を保つため。
- `appsettings.json` バインドは使わない。設定は `Environment.GetEnvironmentVariable` 直読み
  （既存 `line-dotnet` サンプル群の規約踏襲）。未設定でも起動は継続し、該当機能のみ無効化。
- 過剰設計を避ける: 呼び出し元が1箇所しかない抽象化・インターフェースは作らない。
  例外: `Persistence/`配下の`I*Store`群は呼び出し元・実装ともに現状1つずつだが、
  「最小構成から本格実装への移行を容易にする」という明示的な要件のもとで人が承認した抽象化
  （2026-07-17）。新たに同様の抽象化を追加する際は、同水準の明示的な承認を経ること。

## 実装状況

実装計画（設計判断の詳細）は `C:\Users\小林寛忠\.claude\plans\groovy-shimmying-hummingbird.md` を
参照。実装ステップ 1〜9（DI骨組み→Webhook受信→Pet成長ロジック→Flex応答→リッチメニュー→
MINI Appショップ→購入照合→通知フォールバック→E2E確認）を1つずつ進め、各ステップの完了と同時に
`docs/manual/{en,ja}/tutorial.md` の対応章を書く。**各ステップの完了ごとにビルド/テストを確認**し、
全ステップ完了後にのみ3役レビューゲートを通し、人の go/no-go を待ってからコミットする
（実装の途中経過を先行してコミット・マージすることはしない）。
