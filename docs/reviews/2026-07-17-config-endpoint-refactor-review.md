# 2026-07-17 — 設定バインディング・エラーハンドリング・エンドポイント構成リファクタリング レビュー（3役）

前回コミット（`ba2e393`）後、CLAUDE.mdの規約セクション書き換え（「dotnet固有の実装はフレームワーク
推奨を優先」「ASP.NET標準実装を崩す単純化を避ける」）を受けて実施したリファクタリングの3役ゲート。
対象: `CompanionSettings`を`Environment.GetEnvironmentVariable`直読みから
`IConfiguration.Get<T>()`＋`[ConfigurationKeyName]`バインドへ変更、`AddProblemDetails()`/
`UseExceptionHandler()`追加、`appsettings.json`/`appsettings.Development.json`追加、
`/webhook`・`/api/shop/*`ハンドラを`Endpoints/`配下の拡張メソッドへ切り出し。

## 判定

| 役 | 判定 |
|---|---|
| コードレビュアー | CONCERNS（非ブロッキング） |
| セキュリティレビュアー | CONCERNS（非ブロッキング） |
| テスト・アーキ観点 | CONCERNS（**要修正1件**、その他非ブロッキング） |

BLOCKingな指摘なし。ただしテスト・アーキから「Highと明示された、マージ前に直すべき」指摘が1件
あり、実行可能な指摘はすべて反映済み。

## 指摘と対応

| # | 出所 | 指摘 | 重大度 | 対応 |
|---|---|---|---|---|
| 1 | テスト・アーキ | `PollSeconds`の`> 0`検証が新実装で失われており、`LINE_MINIAPP_POLL_SECONDS=0`や負値が正常にバインドされてしまう。`PurchaseReconciliationService.ExecuteAsync`は`PollOnceAsync`用のtry/catchの**外側**で`PeriodicTimer`を構築しており、非正の間隔は`ArgumentOutOfRangeException`を送出——`BackgroundService`の既定動作（`StopHost`）によりアプリ全体がクラッシュする。「未設定・誤設定でも起動は継続し該当機能のみ無効化」という設計方針への直接的な違反 | **High** | **修正**: `CompanionSettings.PollSeconds`にバリデーションするsetterを追加（`value > 0 ? value : 30`）。バインダーはpublicなsetterをリフレクションで呼ぶため、この形でも正しくバインド時に効く。テスト3件追加（`0`/負値→30にフォールバック、非数値→`InvalidOperationException`) |
| 2 | セキュリティ | `builder.Configuration`は既定で`AddCommandLine(args)`を最優先ソースとして含むため、`--LINE_CHANNEL_SECRET=...`のようなコマンドライン引数が環境変数を静かに上書きできてしまう——従来の`GetEnvironmentVariable`直読みでは不可能だった経路 | Medium | **修正**: `CompanionSettings`専用の`IConfiguration`を`BuildCompanionConfiguration()`ローカル関数で明示的に構築（`appsettings.json`→`appsettings.{Environment}.json`→環境変数、`AddCommandLine`は含めない）。Webホスト経路・CLI `setup`経路の両方で同じヘルパーを共有 |
| 3 | コード | `dotnet run -- setup`パスのコメントが「`WebApplicationBuilder`と同じ方法で設定を組み立てる」と主張していたが、実際は`appsettings.{Environment}.json`が抜けていた（パリティの主張が不正確） | Low | **修正**: #2の対応と合わせて、両経路が`appsettings.{Environment}.json`も含む同一ヘルパーを使うよう統一。コメントも実態に合わせて更新 |
| 4 | コード | `Endpoints/`配下の切り出しハンドラで`ILogger<Program>`をDIパラメータとして追加していたが、`static`クラスの拡張メソッド内でも`app`（`WebApplication`）自体はメソッドパラメータとしてクロージャ捕捉可能であり、`app.Logger`をそのまま使えば済んだ（ログカテゴリも変わらず維持できた） | Medium | **修正**: `ILogger<Program>`パラメータを削除し、`app.Logger.LogWarning(...)`に戻した（切り出し前の挙動と完全に一致） |
| 5 | テスト・アーキ | `Endpoints/`への切り出し自体（`MapGroup`/拡張メソッドの配線）に自動テストが無い——`WebhookEndpoints.cs`のコード内コメントが警告する`[FromServices]`必須という失敗モードは、ルートが実際にビルドされる時点でしか顕在化しない | Info | **対応不要（今回は見送り）**: レビュアー自身が「低優先・任意」と明記。`WebApplicationFactory<Program>`はトップレベルステートメントに`public partial class Program`マーカーが必要で追加コストが発生し、代替の軽量な手法も確度が不確かなため、今回は手動スモークテスト（実施済み）に留め、対応不要としてユーザーへ明示的に報告する方針とした |

## 検証

- `dotnet build`: 0警告・0エラー
- `dotnet test`: 29/29 合格（リファクタリング前26/26、指摘#1対応でテスト3件追加）
- 手動スモークテスト: `/`・`/api/shop/catalog`・`/webhook`（未設定時503、`traceId`付きProblemDetails
  形式を確認）・`/api/shop/reserve`（未設定時503）・存在しないルートの404・`dotnet run -- setup`
  （ホスト無しで設定解決→正常終了）を、指摘反映の前後両方で実行中インスタンスに対して確認

## 記録

各役の詳細な指摘全文はこのセッションのサブエージェント出力として生成され、本ファイルには
反映結果のみを要約している（生の指摘全文は保存していない——前回までのレビューと同じ慣行）。

`docs/manual/{en,ja}/tutorial.md`の新規追加節（「Configuration binding, error handling, and
endpoint organization refactor」/「設定バインディング・エラーハンドリング・エンドポイント構成の
リファクタリング」）は本レビュー実施**前**に書かれたものであり、上記の指摘反映（特に#1のPollSeconds
検証、#2のコマンドライン設定ソース除外、#4のロガー変更取り消し）は反映されていない——これは
前回（永続化抽象化）レビューでも同じだった、チュートリアル本文はレビュー前の設計意図を記述し、
レビューで見つかった修正の詳細は`docs/reviews/`側に記録するという、本プロジェクトで既に確立された
書き分けに倣ったもの。

## Go/No-Go

**GO推奨。人の最終go/no-go待ち（未コミット）。**
