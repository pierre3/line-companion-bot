[← Getting started](00-getting-started.md) | [Index](README.md) | [Chapter 2 →](02-webhook.md)

# Chapter 1 — Project skeleton and DI wiring

**What we're building:** the smallest possible app that starts up, reports its own configuration
state, and does nothing else yet. Every later chapter slots a feature into this shape.

**The design rule this establishes:** the app **always starts**, even with nothing configured. Each
LINE feature is gated on whether its required config is present, and a health endpoint reports
what's missing rather than the app refusing to boot. That means you can run any intermediate
chapter and get a useful answer instead of a crash.

## Configuration: bind once from `IConfiguration`

`CompanionSettings` is the app's read-once configuration, bound with the standard .NET mechanism —
`IConfiguration.Get<T>()`. Create `src/LineCompanionBot/CompanionSettings.cs`:

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

Two choices worth calling out:

- **`[ConfigurationKeyName("LINE_...")]` keeps every key a flat environment-variable-style name.**
  The binder maps `LINE_CHANNEL_SECRET` straight onto `ChannelSecret` regardless of the C# property
  name, so the settings read from plain env vars / user-secrets exactly as their names suggest — no
  nested `appsettings.json` section to invent.
- **`PollSeconds` clamps a non-positive value back to 30 in its setter.** [Chapter 7](07-reconciliation.md)
  builds a `PeriodicTimer` from this value *outside* its own poll-failure try/catch, and
  `PeriodicTimer` throws on a non-positive interval — which would crash the whole host. Clamping in
  the setter keeps a `0`/negative typo from being load-bearing. (A *non-numeric* value still throws
  at bind time, deliberately — that's an operator typo worth surfacing loudly, not masking.)

`Get<T>()` is chosen over the fuller `IOptions<T>` Options pattern on purpose: this app never needs
config reload or startup validation (which would fight the "always starts" rule), so the lighter,
equally-standard `Get<T>()` fits without machinery nothing consumes.

## Program.cs: build the config, gate the DI, expose health

Replace the template's `Program.cs` with:

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

Three things to notice:

- **`BuildCompanionConfiguration` is a dedicated config source, not `builder.Configuration`.** It
  deliberately omits `AddCommandLine()` (which `WebApplication.CreateBuilder`'s own configuration
  includes) — a stray `--LINE_CHANNEL_SECRET=` on the command line silently winning would be a
  regression for a security-sensitive value. It also adds user-secrets in
  Development (from Getting started), which is why "put the token in user-secrets" works later.
- **`?? new CompanionSettings()` is load-bearing, not defensive.** `Get<T>()` returns `null` — not a
  defaulted instance — when the configuration is completely empty. Without the fallback, an
  unconfigured first run would `NullReferenceException` instead of starting cleanly.
- **`AddProblemDetails()` + `UseExceptionHandler()`** are the standard .NET pattern for shaping
  unhandled exceptions as `application/problem+json` (with a `traceId`) instead of a bare 500. The
  `Results.Problem(...)` calls in later chapters already produce that shape for *known* errors; this
  closes the gap for unexpected ones.
- **`AddLineMiniApp()` takes no required configuration.** Unlike the webhook/messaging registrations,
  `MiniAppClient`'s methods all take channel/user access tokens as per-call arguments, so there's
  nothing to gate it on — it's always registered.

## appsettings.json

`dotnet new web` already added an `appsettings.json` / `appsettings.Development.json` pair with the
standard `Logging` section. Leave them — the `LINE_*` settings come from env vars / user-secrets in
practice, but having the standard file present is useful for adjusting log levels without an env var,
and `BuildCompanionConfiguration` reads them as its base layer.

## Try it

Press **F5**, then hit the health endpoint (a new VS Code terminal, or a browser):

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

Zero configuration, and the app tells you exactly what to set next — the pattern every later
feature slots into.

## A test for the binding

The binding has a few behaviors worth locking down — the `[ConfigurationKeyName]` mapping, the
`PollSeconds` clamp, and how an unparsable value is handled. Here's the whole test file,
`tests/LineCompanionBot.Tests/CompanionSettingsBindingTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LineCompanionBot.Tests;

public class CompanionSettingsBindingTests
{
    // Mirrors Program.cs exactly: Get<T>() returns null (not a defaulted instance) when the
    // configuration is completely empty, so the "?? new()" fallback is load-bearing, not defensive.
    private static CompanionSettings Bind(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build().Get<CompanionSettings>() ?? new CompanionSettings();

    [Fact]
    public void Get_BindsEachPropertyFromItsFlatLineEnvVarStyleKey()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["LINE_CHANNEL_SECRET"] = "secret",
            ["LINE_CHANNEL_ACCESS_TOKEN"] = "token",
            ["LINE_MINIAPP_LIFF_ID"] = "liff-id",
            ["LINE_MINIAPP_TEMPLATE_NAME"] = "template",
            ["LINE_MINIAPP_POLL_SECONDS"] = "45",
        });

        Assert.Equal("secret", settings.ChannelSecret);
        Assert.Equal("token", settings.ChannelAccessToken);
        Assert.Equal("liff-id", settings.LiffId);
        Assert.Equal("template", settings.TemplateName);
        Assert.Equal(45, settings.PollSeconds);
        Assert.True(settings.HasWebhook);
        Assert.True(settings.HasMessaging);
        Assert.True(settings.HasShop);
    }

    [Fact]
    public void Get_WithNoKeysSet_LeavesEverythingUnconfiguredAndDefaultsPollSeconds()
    {
        var settings = Bind(new Dictionary<string, string?>());

        Assert.Null(settings.ChannelSecret);
        Assert.Null(settings.ChannelAccessToken);
        Assert.Null(settings.LiffId);
        Assert.Null(settings.TemplateName);
        Assert.Equal(30, settings.PollSeconds);
        Assert.False(settings.HasWebhook);
        Assert.False(settings.HasMessaging);
        Assert.False(settings.HasShop);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Get_WithNonPositivePollSeconds_FallsBackTo30(string value)
    {
        var settings = Bind(new Dictionary<string, string?> { ["LINE_MINIAPP_POLL_SECONDS"] = value });

        Assert.Equal(30, settings.PollSeconds);
    }

    [Fact]
    public void Get_WithNonNumericPollSeconds_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["LINE_MINIAPP_POLL_SECONDS"] = "not-a-number" })
                .Build()
                .Get<CompanionSettings>());
    }
}
```

`Bind` is a tiny helper that builds an in-memory `IConfiguration` and binds it exactly as
`Program.cs` does. Each test then pins one behavior:

- **`Get_BindsEachPropertyFromItsFlatLineEnvVarStyleKey`** — every `LINE_*` key maps onto its
  property, `PollSeconds` takes the supplied value, and the three `Has*` flags flip to `true`.
- **`Get_WithNoKeysSet_LeavesEverythingUnconfiguredAndDefaultsPollSeconds`** — with nothing set, the
  string properties stay `null`, `PollSeconds` defaults to 30, and every `Has*` flag is `false` (the
  "always starts, unconfigured" baseline).
- **`Get_WithNonPositivePollSeconds_FallsBackTo30`** — a `0` or negative interval falls back to 30,
  matching the setter's clamp so a typo can't hand `PeriodicTimer` an invalid interval in
  [Chapter 7](07-reconciliation.md).
- **`Get_WithNonNumericPollSeconds_Throws`** — a non-numeric value throws at bind time instead of
  being silently ignored, surfacing an operator typo loudly.

Run them from the **test** task (*Terminal → Run Task → test*, or the Test Explorer), or from a
terminal:

```powershell
dotnet test
```
