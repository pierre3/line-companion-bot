[← はじめに](00-getting-started.md) | [索引](README.md) | [第2章 →](02-webhook.md)

# 第1章 — プロジェクトの骨組みとDI配線

**作るもの:** 起動して自分の設定状態を報告するだけ、それ以外はまだ何もしない——可能な限り最小の
アプリです。以降のすべての章は、この形に機能を差し込んでいくことになります。

**ここで確立する設計ルール:** アプリは、何も設定されていなくても**必ず起動する**。各LINE機能は
必要な設定が揃っているかどうかでゲートされ、起動を拒否する代わりに、ヘルスエンドポイントが「何が
足りていないか」を教えてくれます。おかげで、どの途中段階の章を実行しても、クラッシュではなく有用な
答えが返ってくる、というわけです。

## 設定: `IConfiguration` から一度だけバインドする

`CompanionSettings` は、アプリが起動時に一度だけ読む設定です。バインドには.NET標準の仕組み——
`IConfiguration.Get<T>()`——を使います。`src/LineCompanionBot/CompanionSettings.cs` を作成しましょう:

```csharp
using Microsoft.Extensions.Configuration;

namespace LineCompanionBot;

public sealed class CompanionSettings
{
    [ConfigurationKeyName("LINE_CHANNEL_SECRET")]
    public string? ChannelSecret { get; set; }

    [ConfigurationKeyName("LINE_CHANNEL_ACCESS_TOKEN")]
    public string? ChannelAccessToken { get; set; }

    [ConfigurationKeyName("LINE_MINIAPP_LIFF_ID")]
    public string? LiffId { get; set; }

    [ConfigurationKeyName("LINE_MINIAPP_TEMPLATE_NAME")]
    public string? TemplateName { get; set; }

    private int _pollSeconds = 30;

    [ConfigurationKeyName("LINE_MINIAPP_POLL_SECONDS")]
    public int PollSeconds
    {
        get => _pollSeconds;
        set => _pollSeconds = value > 0 ? value : 30; // non-positive → fall back to 30
    }

    public bool HasWebhook => !string.IsNullOrWhiteSpace(ChannelSecret);
    public bool HasMessaging => !string.IsNullOrWhiteSpace(ChannelAccessToken);
    public bool HasShop => !string.IsNullOrWhiteSpace(LiffId);
}
```

ここで触れておきたい選択が2つあります:

- **`[ConfigurationKeyName("LINE_...")]` によって、すべてのキーがフラットな環境変数風の名前のまま
  になる。** バインダーはC#のプロパティ名に関わらず `LINE_CHANNEL_SECRET` をそのまま
  `ChannelSecret` にマップしてくれるので、設定は名前が示す通り、素の環境変数 / user-secretsから
  読まれます——わざわざネストした `appsettings.json` のセクションをでっち上げる必要はありません。
- **`PollSeconds` はsetter内で非正の値を30へクランプする。** というのも、
  [第7章](07-reconciliation.md)はこの値から `PeriodicTimer` を、自身のポーリング失敗用try/catchの
  *外側*で構築するからです。`PeriodicTimer` は非正の間隔を渡されると例外を投げ——それはホスト全体を
  巻き込んで落としてしまいます。setterでクランプしておけば、`0`/負のタイプミスが致命傷になりません。
  （なお、*数値でない*値のほうはバインド時にやはり例外を投げます——これは意図的です。マスクすべき
  ではなく、大きな音で表面化させるべき操作ミスだからです。）

`Get<T>()` は、より本格的な `IOptions<T>` のOptionsパターンよりも、あえてこちらを選んでいます:
このアプリは設定のリロードも起動時バリデーションも必要としません（後者はそもそも「必ず起動する」
ルールと衝突します）。誰も消費しない仕組みを増やさずに済む、より軽量で、同じく標準的な `Get<T>()`
のほうが、ここには収まりよく合うのです。

## Program.cs: 設定を組み立て、DIをゲートし、ヘルスを公開する

テンプレートの `Program.cs` を、次の内容で置き換えます:

```csharp
using Line.OpenApi.Messaging.DependencyInjection;
using Line.OpenApi.Messaging.Webhook.DependencyInjection;
using Line.OpenApi.MiniApp.DependencyInjection;
using LineCompanionBot;
using Microsoft.Extensions.Configuration;

static IConfiguration BuildCompanionConfiguration(string environmentName)
{
    var configurationBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environmentName}.json", optional: true);

    // User secrets are the framework-recommended local store for the LINE_* secrets in development.
    // Placed before the env-var provider so an explicit env var still wins (standard precedence).
    if (string.Equals(environmentName, "Development", StringComparison.Ordinal))
        configurationBuilder.AddUserSecrets(typeof(Program).Assembly, optional: true);

    return configurationBuilder.AddEnvironmentVariables().Build();
}

var builder = WebApplication.CreateBuilder(args);

var settings = BuildCompanionConfiguration(builder.Environment.EnvironmentName)
    .Get<CompanionSettings>() ?? new CompanionSettings();
builder.Services.AddSingleton(settings);

builder.Services.AddProblemDetails();

// Each Add* is gated so the app always starts; the health endpoint reports what's missing.
if (settings.HasWebhook)
    builder.Services.AddLineWebhook(o => o.ChannelSecret = settings.ChannelSecret!);

if (settings.HasMessaging)
    builder.Services.AddLineMessaging(o => o.ChannelAccessToken = settings.ChannelAccessToken!);

// MiniAppClient takes tokens per call rather than via DI options, so it needs no config to gate on.
builder.Services.AddLineMiniApp();

var app = builder.Build();

app.UseExceptionHandler(); // ProblemDetails-shaped 500s for unhandled exceptions

app.MapGet("/", (CompanionSettings companionSettings) => Results.Ok(new
{
    service = "LineCompanionBot",
    webhook = companionSettings.HasWebhook ? "enabled" : "disabled (set LINE_CHANNEL_SECRET)",
    messaging = companionSettings.HasMessaging ? "enabled" : "disabled (set LINE_CHANNEL_ACCESS_TOKEN)",
    shop = companionSettings.HasShop ? "enabled" : "disabled (set LINE_MINIAPP_LIFF_ID)",
}));

app.Run();
```

ここには注目してほしい点が3つあります:

- **`BuildCompanionConfiguration` は `builder.Configuration` ではなく専用の設定ソースである。**
  あえて `AddCommandLine()`（`WebApplication.CreateBuilder` 自身の設定には含まれます）を省いています
  ——コマンドラインに紛れ込んだ `--LINE_CHANNEL_SECRET=` が静かに勝ってしまうのは、セキュリティに
  敏感な値にとってはリグレッションだからです。同じヘルパーがここと[第5章](05-rich-menu.md)の
  `setup` コマンドの両方に供給されるので、契約は一度だけ定義すれば済みます。加えてDevelopmentでは
  user-secretsを追加しており（「はじめに」より）、これが後で「トークンをuser-secretsに入れる」が
  効いてくる理由です。
- **`?? new CompanionSettings()` は防御ではなく必須である。** というのも、設定が完全に空のとき、
  `Get<T>()` はデフォルト値で埋めたインスタンスではなく `null` を返すからです。このフォールバックが
  無いと、未設定のままの初回実行は、クリーンに起動する代わりに `NullReferenceException` で転んで
  しまいます。
- **`AddProblemDetails()` + `UseExceptionHandler()`** は、未処理例外を素の500ではなく
  `application/problem+json`（`traceId` 付き）に整形してくれる.NET標準パターンです。後の章の
  `Results.Problem(...)` 呼び出しは*既知の*エラーについてすでにこの形状を返しますが、こちらは
  予期しない例外側のギャップを埋めてくれます。
- **`AddLineMiniApp()` は必須設定を取らない。** webhook/messagingの登録とは違い、`MiniAppClient` の
  メソッドはすべてチャネル/ユーザーアクセストークンを呼び出し毎の引数として受け取ります。だから
  ゲートすべき対象がなく——常に登録されるわけです。

## appsettings.json

`dotnet new web` は、標準の `Logging` セクションを持つ `appsettings.json` /
`appsettings.Development.json` のペアを、すでに追加してくれています。これはそのまま残しておいて
ください——`LINE_*` 設定は実運用では環境変数 / user-secretsから来ますが、標準ファイルがあると
環境変数無しでログレベルを調整するのに便利ですし、`BuildCompanionConfiguration` もそれらをベース
レイヤーとして読み込みます。

## 動かしてみる

**F5**を押して、ヘルスエンドポイントを叩いてみましょう（新しいVS Codeターミナル、またはブラウザ
から）:

```powershell
Invoke-RestMethod http://localhost:5091/
```

```json
{
  "service": "LineCompanionBot",
  "webhook": "disabled (set LINE_CHANNEL_SECRET)",
  "messaging": "disabled (set LINE_CHANNEL_ACCESS_TOKEN)",
  "shop": "disabled (set LINE_MINIAPP_LIFF_ID)"
}
```

設定がゼロの状態でも、アプリは次に何を設定すればよいかを正確に教えてくれます——これが、以降の
すべての機能が差し込まれていくパターンになります。

## バインディングのテスト

このバインディングには、テストを1つ書いておく価値があるだけの機微があります（空のとき`null`になる
挙動、`PollSeconds` のクランプ）。`tests/LineCompanionBot.Tests/CompanionSettingsBindingTests.cs`
を追加しましょう:

```csharp
private static CompanionSettings Bind(Dictionary<string, string?> values)
    => new ConfigurationBuilder().AddInMemoryCollection(values).Build()
        .Get<CompanionSettings>() ?? new CompanionSettings();

[Fact]
public void Get_BindsEachPropertyFromItsFlatLineEnvVarStyleKey() { /* asserts LINE_* → properties */ }

[Fact]
public void Get_WithNoKeysSet_LeavesEverythingUnconfiguredAndDefaultsPollSeconds() { /* asserts null + 30 */ }

[Theory, InlineData("0"), InlineData("-5")]
public void Get_WithNonPositivePollSeconds_FallsBackTo30(string value) { /* ... */ }

[Fact]
public void Get_WithNonNumericPollSeconds_Throws() { /* Assert.Throws<InvalidOperationException> */ }
```

実行はTestタスク（`tasks.json` → `test`）または `dotnet test` から行います。実は、この空設定の
テストを書いてみたことこそが、上記の `?? new()` がなぜ要るのかを浮かび上がらせてくれたのでした。
