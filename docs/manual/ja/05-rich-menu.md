[← 第4章](04-flex-postback.md) | [索引](README.md) | [第6章 →](06-shop.md)

# 第5章 — `line` ツールでリッチメニューを登録する

**このステップで作るもの:** 今回はアプリのコードではありません。リッチメニューの*定義*（`richmenu.json`）
を用意し、それを `Line.OpenApi.Tools` というコマンドラインツールで LINE に登録します。これが、第4章の
postback 文字列（`"action=feed"` 等）を、ユーザーが実際に指でタップできるものへ変える最後のピースです。

`Line.OpenApi.*` ファミリには `Line.OpenApi.Tools` というコマンドラインツールが付属しており、その
`richmenu` コマンドでリッチメニューを一通り管理できます。定義からの作成、画像のアップロード、デフォルト
設定、一覧・削除までです。この登録はアプリのコードを書かず、このツールで行います。

## ツールをインストールする

`Line.OpenApi.Tools` は .NET グローバルツール（コマンド名 `line`）であり、同時に MCP サーバでもあります:

```powershell
dotnet tool install -g Line.OpenApi.Tools --version 0.2.0-preview
```

`line --help` でコマンドグループ（`richmenu`、`config` …）が並べば成功です。（`line-dotnet` のソースを
チェックアウトしているなら、インストールせずに `dotnet run --project path/to/line-dotnet/tools/Line.OpenApi.Tools -- <command>`
としても同じことができます。）

## リッチメニューの定義

`line richmenu create` は JSON 定義（LINE 標準のリッチメニュー形状）を受け取ります。
`src/LineCompanionBot/assets/richmenu.json` を作成します:

```json
{
  "size": { "width": 2500, "height": 1686 },
  "selected": true,
  "name": "LineCompanionBot default menu",
  "chatBarText": "Menu",
  "areas": [
    {
      "bounds": { "x": 0, "y": 0, "width": 1250, "height": 843 },
      "action": { "type": "postback", "data": "action=feed" }
    },
    {
      "bounds": { "x": 1250, "y": 0, "width": 1250, "height": 843 },
      "action": { "type": "postback", "data": "action=play" }
    },
    {
      "bounds": { "x": 0, "y": 843, "width": 1250, "height": 843 },
      "action": { "type": "postback", "data": "action=status" }
    },
    {
      "bounds": { "x": 1250, "y": 843, "width": 1250, "height": 843 },
      "action": { "type": "uri", "uri": "https://liff.line.me/YOUR_LIFF_ID" }
    }
  ]
}
```

2500×1686 のキャンバスを四分割した、4つのタップ領域です:

- **Feed / Play / Status** は `postback` 領域で、その `data` は第4章でWebhookが分岐に使っている文字列
  ([第4章](04-flex-postback.md)) そのものです。ここでようやく、その文字列に送り手が付きます。この `data`
  と `WebhookEndpoints.cs` の `switch` case は手動で対応させているので、片方の名前を変えたらもう片方も
  変えないと、タップしても何も起きません。
- **Shop** は `uri` 領域で、MINI App の LIFF URL を開きます（postback は送らず、LINE がURLを開くだけ）。
  `YOUR_LIFF_ID` はあなたの MINI App の LIFF id に置き換えてください（[第6章](06-shop.md)/
  [第9章](09-end-to-end.md) の後に手に入ります）。

## 画像

`richmenu.json` はタップ領域を記述しますが、メニューには背景画像、つまりディスク上の実物のPNGも必要です。
実際のピクセルをアップロードする以外に道はありません。`src/LineCompanionBot/assets/` に `richmenu.png` を
置いてください。参照リポジトリの
[`src/LineCompanionBot/assets/richmenu.png`](https://github.com/pierre3/line-companion-bot/blob/main/src/LineCompanionBot/assets/richmenu.png)
からプレースホルダをコピーしても、自分で作ってもかまいません。このプロジェクトには画像生成ライブラリが
無い（四角を4つ描くために足すのは不釣り合いな依存です）ので、プレースホルダは使い捨ての PowerShell +
`System.Drawing` スクリプトで一度だけ、アプリの外で生成しました（ビルド時の成果物であって、アプリの一部
ではありません）:

```powershell
Add-Type -AssemblyName System.Drawing
# ...2500x1686 のキャンバスに、ラベル付きの 1250x843 四分割（FEED / PLAY / STATUS / SHOP）を描く...
$bmp.Save("assets/richmenu.png", [System.Drawing.Imaging.ImageFormat]::Png)
```

デモを超えて使う前に、本物のアートワークに差し替えてください。埋め込みアセットとは違い、ツールは画像パスを
明示的に（`--file` で）受け取るので、`.csproj` でのコピー設定に悩む必要はありません。

## 登録する

3ステップとも、チャネルアクセストークンが要ります。渡し方は環境変数
（`LINE_CHANNEL_ACCESS_TOKEN`）、コマンドごとの `--channel-token`、保存済みプロファイル
（`line config set default --token "..."`、`~/.line/config.json` に保存）のいずれでもかまいません。
どの方法でもトークンはシェル環境か平文ファイルに残るので、失効させられるトークンを使い、用が済んだら
消しておくとよいでしょう。そのうえで、リポジトリ直下から:

```powershell
# 1. 定義からメニューを作成。新しいリッチメニュー id が出力される。
line richmenu create --file src/LineCompanionBot/assets/richmenu.json

# 2. その id に背景画像をアップロード。
line richmenu image <richMenuId> --file src/LineCompanionBot/assets/richmenu.png

# 3. チャネルの全ユーザーのデフォルトメニューに設定。
line richmenu set-default <richMenuId>
```

`create` が出力する `richMenuId` を、ステップ2・3に貼り付けます。画像アップロードは LINE の*データ*ホスト
（`api-data.line.me`）、つまりコントロール系の呼び出しとは別ホストへ向かいますが、ツールがルーティングして
くれるので、低レベルクライアントなら自分で設定が必要になる BaseUrl の切り替えを気にせずに済みます。

## 同じツールを MCP サーバとして使う（任意）

`line` は MCP サーバも兼ねているので、シェルの代わりに Claude Code から同じ操作を駆動できます:

```powershell
claude mcp add line -- line mcp
```

これで `line_richmenu_create`、`line_richmenu_set_default`、`line_richmenu_list` などが MCP ツールとして
使えます。ひとつ意図的な欠落があります。**画像アップロードは CLI 専用**です（バイナリを MCP 越しに流すのは
非現実的なため）。したがって MCP 駆動のフローでも、`line richmenu image` のステップだけは CLI で実行します。

## 番外編 — AIエージェントに定義と画像を作らせる（任意）

エージェント（Claude Code）があるなら、もう一歩踏み込めます。`richmenu.json` と `richmenu.png` を*手で*
用意する代わりに、エージェントに作らせるのです。ここで役割を混同しないのが肝心です。**JSON と画像を*作る*
のはエージェント自身の能力**で、`line` MCP サーバがやるのは*登録*（`line_richmenu_create` /
`line_richmenu_set_default`）だけ、という切り分けです。3つのプロンプトで一気通貫にできます。

**① 定義を書かせる。** 第4章の分岐と一致させる指示を必ず添えます。ここを外すと、タップしても何も
起きません:

> `src/LineCompanionBot/assets/richmenu.json` を作って。2500×1686 を4分割。左上=Feed / 右上=Play /
> 左下=Status は `postback` で `data` をそれぞれ `action=feed` / `action=play` / `action=status`
> （`WebhookEndpoints.cs` の `switch` case と一致させること）。右下=Shop は `uri` アクションで
> `https://liff.line.me/YOUR_LIFF_ID`。`name` は "LineCompanionBot default menu"、`chatBarText` は
> "Menu"、`selected` は true。

**② プレースホルダ画像を作らせる。** 本文が使い捨ての System.Drawing スクリプトで PNG を起こしたのと
同じことを、言葉で頼むだけです:

> 上の `richmenu.json` のレイアウトに合わせて、2500×1686 のプレースホルダPNGを
> `src/LineCompanionBot/assets/richmenu.png` に生成する使い捨てスクリプトを書いて実行して。4象限を
> 薄く色分けし、中央に FEED / PLAY / STATUS / SHOP を大きく描いて（Windowsなら System.Drawing）。

**③ そのまま MCP で登録させる。** これは実チャネル操作なのでトークンが要り、実行は第9章の仕事です:

> `line` MCP が設定済みなら、続けて `line_richmenu_create` で登録して。画像アップロードだけは MCP に
> 無いので、`line richmenu image <id> --file …` を CLI で実行し、最後に `line_richmenu_set_default` で
> デフォルトに設定して（create → image → set-default の順で）。

①②はトークン不要でいま試せますが、生成物は必ず目視してください。postback の `data` が
`WebhookEndpoints.cs` の `case` と一致しているか、そして Shop の `uri` が `YOUR_LIFF_ID` プレースホルダ
のまま残っているか（エージェントがそれらしい id を勝手に埋めていないか。実 id は第9章で入れます）。
③の登録は CLI 手順とまったく同じく実トークンが要るので、[第9章](09-end-to-end.md)の仕事です。

## 試してみる

`line richmenu` 系はどれもトークン付きで LINE を呼ぶので、ここで完全にオフラインで試せるのは
`line --help` と `richmenu.json` の目視くらいです。メニューを実際に登録する上の3コマンドは、実チャネルを
設定する[第9章](09-end-to-end.md)の仕事です。すでにチャネルアクセストークンをお持ちなら、いま実行すれば、
ボットを友だち追加した瞬間にメニュー（Feed / Play / Status / Shop）が現れます。
