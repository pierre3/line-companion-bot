[← 第8章](08-notify.md) | [索引](README.md)

# 第9章 — 実チャネルでのエンドツーエンドとトラブルシューティング

これまでの各章では、それぞれの部品をローカルで確かめてきました——署名のラウンドトリップ、postbackの
ディスパッチ、Flexの構築、`setup` verb、ショップのHTTP契約、ポーリングと再試行のループ——いずれも
実LINEチャネル抜きで、です。さて、この章はその残りの部分にあたります。すべてを一緒に動かすために
実チャネルを配線すること、そして——正直なところ——何がうまくいかなくなりがちか、という話です。

## コンソール設定——行き詰まりを避ける順序で

1. **Messaging APIチャネルを作成する。**
   [LINE Developers Console](https://developers.line.biz/console/) で作成し、**channel secret** を
   控えたうえで、**channel access token** を発行します。
2. **LINE MINI Appチャネルを作成する。** 同じプロバイダーの下に作成します。これは通常のLIFFアプリ
   とは別個のプロダクトで、独自の審査 / トライアルユーザーのフローを持っています——full review なしで
   テストできるよう、自分自身を **trial user** として追加しておいてください。割り当てられた
   **LIFF ID** を控えます。
3. 実はこの順序を取り違えること——MINI Appチャネルが、Messaging APIチャネルとは別個の独自プロバイダー
   設定を必要とする、という点を理解する前に登録しようとしてしまうこと——が、ここでいちばん起こりやすい
   現実のつまずきどころです。コードの中のどんな落とし穴よりも、です。

## シークレットを user-secrets に入れる

Getting started でも触れたとおり、シークレットは `dotnet user-secrets` に置き、チェックインされる
ファイルには決して置きません:

```powershell
dotnet user-secrets set LINE_CHANNEL_SECRET       "<channel secret>"       --project src/LineCompanionBot
dotnet user-secrets set LINE_CHANNEL_ACCESS_TOKEN "<channel access token>" --project src/LineCompanionBot
dotnet user-secrets set LINE_MINIAPP_LIFF_ID      "<liff id>"              --project src/LineCompanionBot
# optional, for the service-message path instead of push:
# dotnet user-secrets set LINE_MINIAPP_TEMPLATE_NAME "<approved template name>" --project src/LineCompanionBot
```

`BuildCompanionConfiguration` が user-secrets を読むのは `Development` 環境のときだけです。とはいえ
F5の起動構成も `setup-richmenu` タスクも `ASPNETCORE_ENVIRONMENT=Development` を設定してくれるので、
どちらの経路からでもこれらはちゃんと拾われます。

## 立ち上げる

1. **リッチメニューを一度だけ作成する:** **setup-richmenu** タスクを実行します（*Terminal → Run
   Task → setup-richmenu*）。うまくいけばリッチメニューidが表示されるはずです。
2. **アプリを起動する:** **F5** を押すだけです。
3. **devトンネルで公開する** ——LINEがこちらのwebhookに到達できるようにするためです:

   ```powershell
   devtunnel user login       # first time only
   devtunnel host -p 5091 --allow-anonymous
   ```

4. 転送されたHTTPS URL に `/webhook` を足したものを、コンソールでチャネルの **Webhook URL** に設定し、
   **Use webhook** をオンにしたうえで、**Verify** をクリックします。

## フルループを試す

1. Botを友だち追加します。すると、リッチメニュー（Feed / Play / Status / Shop）が即座に表示される
   はずです——setupタスクが効いている、何よりの証拠ですね。
2. **Feed / Play / Status** をタップしてみます——それぞれ約1秒でFlexのステータスカードが返ってきます。
   Hungerが低い状態（減衰はリアルタイムです）で **Play** をタップすると、例の拒否カードが顔を出します。
3. **Shop** をタップしてMINI Appを開きます。カタログが読み込まれ、アイテムを購入できます
   （`liff.iap.createPayment` が、実際のApp Store / Play Storeの購入UIを駆動します——第6章）。
4. 購入が完了すると、`PurchaseReconciliationService` が次のポーリングtickでそれを拾い上げ
   （`LINE_MINIAPP_POLL_SECONDS`、デフォルト30——ここは **即時ではありません**、push webhookが無い
   ためです）、新しいアイテムを知らせるチャットメッセージが届きます。試しに **Golden Kibble** を
   買ってから **Feed** をタップすると、Hungerが満タンまで回復し、そのぶん消費されるのが分かります。

なお、この間ずっとVS Codeのデバッガをアタッチしたままにしておけます——`WebhookEndpoints` や
`PurchaseReconciliationService` にブレークポイントを置いて、実際のLINEトラフィックが流れていく様子を
じっくり観察できます。

## トラブルシューティング

- **リッチメニューが出ない / タップしても何も起きない。** まず、setupタスクがリッチメニューidを表示
  したこと（「not set」メッセージではないこと）を確かめてください。そのうえで `GET /` が
  `messaging: enabled` を報告しているかも見ておきます。
- **`/webhook` で401。** これは `LINE_CHANNEL_SECRET` がチャネルのものと一致していないサインです。
- **Feed/Play/Status が何もしない。** ログに "Failed to reply to a postback event" が出ていないか
  確認します——たいていは、テストがもたついて期限切れになったreplyトークン（有効期間は約1分です）か、
  欠落した/無効なアクセストークンが原因です。
- **Shopボタンが空白ページを開く。** `LINE_MINIAPP_LIFF_ID` が間違っているか、MINI Appチャネルの
  エンドポイントURLがこのアプリの `/shop/` パスを向いていないかのどちらかです——コードの問題では
  なく、コンソール設定の問題ですね。
- **購入は完了するのにチャットメッセージが来ない。** 最大で `LINE_MINIAPP_POLL_SECONDS` ほどかかるのが
  想定どおりの挙動です——即時pushは無いのでした。それでも一向に届かないようなら、
  `PurchaseReconciliationService` のwarningを確認してください（無効/期限切れのトークンが、たいていの
  原因です）。
- **サービスメッセージが一度も送られず、常にPushへフォールバックする。** これは `LINE_MINIAPP_TEMPLATE_NAME`
  が *承認済み* テンプレートであること、かつユーザーが有効なnotifierトークンを取れるほど最近ショップを
  開いていること——この両方が揃わない限り、むしろ想定どおりの挙動です（第8章を参照）。Pushフォール
  バックは意図されたデフォルトの安全な経路であって、バグではありません。

## 確認済みのこと・実チャネルが必要なこと

実のところ、第8章までのすべてはローカルで確認できます。署名検証（受理 *と* 拒否の両方）、
`PetGrowthEngine` へのpostbackディスパッチが実際のFlex Messageを生成すること、`setup` verb の
ディスパッチとトークンなしでのクリーンな終了、すべてのショップエンドポイント（config/catalog/inventory
に reserve のバリデーション分岐まで）、そして照合ループが実際に `api.line.me` に到達し、クラッシュせず
にレスポンスを処理すること——ここまでは手元で見届けられます。残るもの——チャット返信が実際に届くこと、
リッチメニューがレンダリングされること、完了したIAP購入が grant→notify の全経路を駆動すること——には、
上記の実チャネル設定がどうしても必要になります。だからこそこの章は、「信じてくれ、動くから」を前の各章
に畳み込んでしまうのではなく、あえて別立てで存在しているのです。

## レビューゲートについての注記

最後に一つ。このアプリを完成とみなす前に、3役のレビューゲート（コード / セキュリティ / テスト・アーキ）
を通しています。いずれも **CONCERNS（非ブロッキング）** を返し、対応可能な指摘はすべて修正済みです。
そしてそれらの修正は、別立ての付録にまとめたのではなく——それぞれが関係する章の中に畳み込んであります。
LINE自身の `ev.UserId` を信頼する照合（第7章）、引き締めたnotifyフォールバックとその `Error` レベルの
二重失敗ログ（第8章）、インベントリの読み取りロックと、アイテムに実際の効果を持たせるGolden Kibbleの
消費（第6章および第3〜4章）、そして追加した `PetGrowthEngine` のテストケース（第3章）といった具合です。
つまりここまでで組み上げたコードは *そのまま* レビュー済みの最終形であり——あとから学び直さなければ
ならないものは、何一つ残っていません。
