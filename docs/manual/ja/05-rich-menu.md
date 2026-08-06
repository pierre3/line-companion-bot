[← 第4章](04-flex-postback.md) | [索引](README.md) | [第6章 →](06-shop.md)

# 第5章 — リッチメニューのブートストラップ (`dotnet run -- setup`)

**このステップで作るもの:** リッチメニューを作成し、その画像をアップロードし、アカウントの
デフォルトに設定する使い捨てのCLIコマンドです——第4章で用意したpostback文字列（`"action=feed"`等）を、
ユーザーが実際に指でタップできるものへと変える、最後のピースにあたります。

**なぜHTTPエンドポイントではなくCLIコマンドなのでしょうか。** 理由はシンプルで、*デフォルト*の
リッチメニュー設定がアカウント全体に——つまりチャネルの全ユーザーに——及ぶからです。これは
れっきとした管理操作ですが、このアプリはWebhookを機能させるためにdev tunnel経由でインターネットに
公開されています。ここで仮に`POST /setup`エンドポイントを作ってしまうと、破壊的で未認証の管理操作を、
Webhookと同じ公開面にそのまま並べることになってしまいます。そこで`WebApplication`が構築される
*前*に`args[0]`で分岐させ、この操作を厳密にローカル限定へと留めておくわけです。

## Program.cs のsetupコマンド

`Program.cs`の一番上、`WebApplication.CreateBuilder`より前に、コマンドの分岐を足していきます。
第1章と同じ`BuildCompanionConfiguration`ヘルパーを再利用しているので、user-secretsや環境変数は
Webホストのときとまったく同じように解決されます:

```csharp
if (args.Length > 0 && args[0] == "setup")
{
    var setupEnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
    var setupSettings = BuildCompanionConfiguration(setupEnvironmentName).Get<CompanionSettings>() ?? new CompanionSettings();
    await RichMenuBootstrapper.RunAsync(setupSettings, "assets/richmenu.png");
    return;
}
```

というのも、このパスにはホストが存在せず、そのまま使い回せるアンビエントな`IConfiguration`も
無いからです——共有ヘルパー経由で自前に組み立てているのは、まさにこのヘルパーが存在する理由
そのものだと言えます。環境名を`ASPNETCORE_ENVIRONMENT`から取得している点には少し注意して
ください。`.vscode/tasks.json`の`setup-richmenu`タスクがこれを`Development`に設定してくれるので、
user-secretsに入れたトークンがきちんと拾われます。

## ブートストラッパー

`src/LineCompanionBot/Services/RichMenuBootstrapper.cs`を作っていきましょう:

```csharp
public static async Task RunAsync(CompanionSettings settings, string imagePath)
{
    if (!settings.HasMessaging)
    {
        Console.Error.WriteLine("LINE_CHANNEL_ACCESS_TOKEN is not set — cannot create a rich menu.");
        return;
    }

    var shopUri = settings.HasShop ? $"https://liff.line.me/{settings.LiffId}" : "https://line.me/";

    var request = new RichMenuRequest
    {
        Name = "LineCompanionBot default menu",
        ChatBarText = "Menu",
        Selected = true,
        Size = new RichMenuSize { Width = 2500, Height = 1686 },
        Areas = new List<RichMenuArea>
        {
            Area(0, 0, "action=feed"),
            Area(HalfWidth, 0, "action=play"),
            Area(0, HalfHeight, "action=status"),
            AreaUri(HalfWidth, HalfHeight, shopUri), // the LIFF shop URL from Chapter 6
        },
    };

    var richMenu = RichMenuClient.CreateWithStaticToken(settings.ChannelAccessToken!);
    var richMenuId = await richMenu.CreateAsync(request);
    if (richMenuId is null) { Console.Error.WriteLine("Failed to create the rich menu (no id returned)."); return; }

    await richMenu.SetImageFromFileAsync(richMenuId, imagePath);
    await richMenu.SetDefaultAsync(richMenuId);
    Console.WriteLine($"Rich menu created and set as default: {richMenuId}");
}
```

`Area` / `AreaUri`は、4つの象限の`RichMenuArea`を組み立てるための小さなヘルパーです。このうち
3つは`PostbackAction`を使っていて（Webhookが既に分岐させている`"action=..."`文字列と対応します）、
4つ目だけはMINI AppショップのLIFF URLを指す`URIAction`を使います——ショップボタンにpostbackは
必要なく、LINEは単純にそのURLを開くだけだからです。

`RichMenuClient.CreateWithStaticToken(...)`は、リッチメニュー系エンドポイントのファサードです。
ここで特に効いてくるのが、画像のアップロードがLINEの*data*ホスト（`api-data.line.me`）を叩く、
という事情です。このホストはクライアント構築より前に設定しておく必要があるのですが——ファサードが
内部でそれを面倒みてくれるおかげで、低レベルの`MessagingClient.Blob`が要求するBaseUrlの設定順序を、
こちらで意識せずに済みます。

## ブロッキングな前提条件: 実際の画像ファイル

`SetImageFromFileAsync`は、ディスク上に実在するPNGを必要とします——実ピクセルのアップロードを
避けて通る道はありません。とはいえ、このリポジトリには画像生成ライブラリが入っていません
（4つの四角を描くためだけに足すには、あまりに不釣り合いな依存でしょう）。そこで
`assets/richmenu.png`のプレースホルダーは、アプリの一部ではない使い捨ての PowerShell +
`System.Drawing`スクリプトで、一度だけout-of-bandで生成してあります（アプリの一部ではなく、
あくまでビルド時の成果物という位置づけです）:

```powershell
Add-Type -AssemblyName System.Drawing
# ...draw four labeled 1250x843 quadrants (FEED / PLAY / STATUS / SHOP) on a 2500x1686 canvas...
$bmp.Save("assets/richmenu.png", [System.Drawing.Imaging.ImageFormat]::Png)
```

デモの範囲を超えて使うのであれば、このファイルを実際のアートワークに差し替えてください。その際、
`.csproj`がこのファイルを出力先へコピーするようにしておくのを忘れずに——さもないと、ビルド
ディレクトリから実行したときに相対パス`"assets/richmenu.png"`が解決できなくなってしまいます。

## 動かしてみる — 配線の確認に実チャネルは不要

**setup-richmenu**タスクを実行するか（VS Code: *Terminal → Run Task → setup-richmenu*）、
あるいはターミナルから直接実行してみましょう:

```powershell
dotnet run --project src/LineCompanionBot -- setup
```

トークンを未設定のまま実行すると、こうなります:

```
LINE_CHANNEL_ACCESS_TOKEN is not set — cannot create a rich menu.
```

このコマンドはWebホストが起動する前に分岐して、そのままクリーンに終了しました——サーバは
立ち上がらず、クラッシュもしません。実チャネルアクセストークンに対して実行すれば（第9章）、
今度は実際にメニューが作成・有効化されることになります。
