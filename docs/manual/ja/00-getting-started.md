[← 索引](README.md) | [第1章 →](01-project-skeleton.md)

# はじめに — ゼロから雛形を作る

LINE固有のコードに手を付ける前に、この章ではまず、空のフォルダからVisual Studio Codeで実行・
デバッグできるASP.NET Coreアプリまでを立ち上げてみましょう。この段階ではまだ`Line.OpenApi.*`固有の
ものは何ひとつ登場しません——以降のすべての章が土台にすることになる、素の.NETプロジェクトの形です。

## 前提条件

- **.NET 10 SDK** — `dotnet --version` で確認しておきましょう（`10.*` が表示されるはずです）。
- **Visual Studio Code** と **C# Dev Kit** 拡張（`ms-dotnettools.csdevkit`）。このチュートリアルが
  前提にしているデバッガ・テストランナー・ソリューションビューを、これがまとめて用意してくれます。
  この後で自分で追加する `.vscode/extensions.json` を置いておけば、フォルダを開いたときにVS Codeが
  インストールをうながしてくれます。
- LINE Messaging APIチャネルとMINI Appチャネルは、[第9章](09-end-to-end.md)まで**要りません**。
  それ以前はすべて、LINEアカウント無しで動きます。

## ソリューションとプロジェクトを作る

リポジトリを置くディレクトリから、次を実行します:

```powershell
dotnet new sln -n LineCompanionBot

# The web app (SDK: Microsoft.NET.Sdk.Web), targeting net10.0.
dotnet new web -o src/LineCompanionBot -f net10.0

# A test project — Chapter 3 is the one piece of this app worth unit-testing.
dotnet new xunit -o tests/LineCompanionBot.Tests -f net10.0

dotnet sln add src/LineCompanionBot tests/LineCompanionBot.Tests
dotnet add tests/LineCompanionBot.Tests reference src/LineCompanionBot
```

`dotnet new web` が生成するのは、最小のASP.NET Coreテンプレート——"Hello World!"を返す1行の
`Program.cs`——です。第1章で置き換えてしまいますが、いまは動作確認済みの出発点として役立ちます。

続いて、アプリプロジェクトに`Nullable`と`ImplicitUsings`を設定しておきます（どちらも後の章が
頼りにします）。`src/LineCompanionBot/LineCompanionBot.csproj` を編集します:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```

## LINEパッケージを追加する

ここで `Line.OpenApi.*` のうち3つを消費します——NuGetの**PackageReference**として、実際の消費者が
使うのとまったく同じ形でです（このリポジトリは意図的に、ライブラリ自身のソースツリーから独立させて
あります）:

```powershell
dotnet add src/LineCompanionBot package Line.OpenApi.Messaging --version 0.2.0-preview
dotnet add src/LineCompanionBot package Line.OpenApi.Messaging.Webhook --version 0.2.0-preview
dotnet add src/LineCompanionBot package Line.OpenApi.MiniApp --version 0.2.0-preview
```

`Line.OpenApi.Liff` は、あえて参照*しません*——このアプリはそれを一度も呼ばないからで、呼ばない
依存はただのノイズになるだけです。役割分担はこうです: `Messaging` がreply/push/リッチメニューを、
`Messaging.Webhook` が署名検証とペイロード解析を、`MiniApp` がショップのreserve/notifier/IAP
ポーリングを、それぞれ受け持ちます。

## VS Codeで開いて実行・デバッグを設定する

フォルダを開きます（`code .`）。`.vscode/` フォルダを作成し、F5実行を駆動する3ファイルを置きます。
リファレンス実装リポジトリの
[`.vscode/`](https://github.com/pierre3/line-companion-bot/tree/main/.vscode) にある
`launch.json`・`tasks.json`・`extensions.json` を、そのまま自分のプロジェクトの `.vscode/` に
コピーしてください:

- **`launch.json`** — 単一の「Run LineCompanionBot」構成です。まずビルドし（`preLaunchTask`）、
  デバッガをアタッチしてアプリのDLLを起動し、`ASPNETCORE_ENVIRONMENT=Development` を設定し、
  プロジェクトフォルダを作業ディレクトリとして実行します。おかげで `appsettings.json` が相対パスで
  解決されます。
- **`tasks.json`** — `build`・`test` の各タスク。（第5章のリッチメニュー登録は、VS Code タスクではなく
  独立した `line` グローバルツールを使います。）
- **`extensions.json`** — C# Dev Kitを推奨します。

## シークレット: チェックインするファイルではなく `dotnet user-secrets` を使う

LINEのチャネルシークレットとアクセストークンは機密情報ですから、`appsettings.json` や `launch.json`
には決して入れてはいけません。フレームワークが推奨するローカルの保管場所は**ユーザーシークレット**
——リポジトリツリーの外に置かれ、`UserSecretsId` でプロジェクトに紐づく `secrets.json` です。

有効化は一度だけで済みます:

```powershell
dotnet user-secrets init --project src/LineCompanionBot
```

これで`.csproj`に`<UserSecretsId>`（生成されたGUID）が書き込まれます——値そのものは安定した識別子で
あればよく、このリポジトリがたまたま読みやすい文字列を使っていても問題ありません。実際のシークレットを設定するのは
[第9章](09-end-to-end.md)まで不要ですが（それ以前に実トークンを必要とするものは何もありません）、
いまのうちにこの仕組みを配線しておけば、後の章で「user-secretsに入れる」と、いちいち前置きを添えずに
言えるようになります。第1章では、`CompanionSettings` がこの保管場所から読むようにする`Program.cs`の
1行をお見せします。

## 最初の実行

それでは**F5**を押してみましょう。VS Codeがビルド・起動し、（`serverReadyAction` により）待ち受け
URLでブラウザを開いてくれます。デフォルトテンプレートは `http://localhost:5091/` で `Hello World!`
を返します。試しに `Program.cs` にブレークポイントを置いてリロードし、デバッガがちゃんと止まることを
確かめてみてください——これが、ここから各章で回していく内側ループのすべてです。

確認できたらアプリを停止します（赤い四角、または `Shift+F5`）。次章では、このHello-Worldの骨組みを、
本物の設定とDIの形に置き換えていきます。
