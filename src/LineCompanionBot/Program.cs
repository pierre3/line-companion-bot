using Line.OpenApi.Messaging.DependencyInjection;
using Line.OpenApi.Messaging.Webhook.DependencyInjection;
using Line.OpenApi.MiniApp.DependencyInjection;
using LineCompanionBot;
using LineCompanionBot.Endpoints;
using LineCompanionBot.Persistence.InMemory;
using LineCompanionBot.Services;
using Microsoft.Extensions.Configuration;

// CompanionSettings is bound from its own configuration source — appsettings.json +
// appsettings.{Environment}.json + environment variables — deliberately built without
// AddCommandLine(), unlike the web host's own builder.Configuration (which includes it by
// default). LINE_CHANNEL_SECRET/LINE_CHANNEL_ACCESS_TOKEN are security-sensitive and this
// project's design has always been env-var-only for them; letting a stray "--LINE_CHANNEL_SECRET="
// argv entry silently win would be a regression from that. Both the CLI "setup" path below (no
// host at all) and the web host path use this same helper, so the contract is defined once.
static IConfiguration BuildCompanionConfiguration(string environmentName) => new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

// "dotnet run -- setup": one-shot rich menu bootstrap. Handled before WebApplication is built —
// this is a local admin action, never an HTTP endpoint reachable over a dev tunnel.
if (args.Length > 0 && args[0] == "setup")
{
    var setupEnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
    var setupSettings = BuildCompanionConfiguration(setupEnvironmentName).Get<CompanionSettings>() ?? new CompanionSettings();
    await RichMenuBootstrapper.RunAsync(setupSettings, "assets/richmenu.png");
    return;
}

var builder = WebApplication.CreateBuilder(args);

// IConfiguration.Get<T>() is the standard binding mechanism for read-once startup configuration;
// [ConfigurationKeyName] on CompanionSettings keeps every key exactly LINE_* as before, so this is
// still plain-env-var-driven in practice.
var settings = BuildCompanionConfiguration(builder.Environment.EnvironmentName).Get<CompanionSettings>() ?? new CompanionSettings();
builder.Services.AddSingleton(settings);

builder.Services.AddProblemDetails();

// Each Add* call is gated so the app always starts, even with nothing configured yet — the health
// endpoint below reports what's missing rather than the app refusing to boot.
if (settings.HasWebhook)
{
    builder.Services.AddLineWebhook(o => o.ChannelSecret = settings.ChannelSecret!);
}

if (settings.HasMessaging)
{
    builder.Services.AddLineMessaging(o => o.ChannelAccessToken = settings.ChannelAccessToken!);
}

// MiniAppClient takes tokens per call rather than via DI options, so this has no required config.
builder.Services.AddLineMiniApp();

builder.Services.AddInMemoryPersistence();
builder.Services.AddHostedService<PurchaseReconciliationService>();

var app = builder.Build();

app.UseExceptionHandler(); // ProblemDetails-shaped 500s for unhandled exceptions, per AddProblemDetails() above

app.UseStaticFiles(); // serves wwwroot/shop/* (the MINI App front-end)

app.MapGet("/", (CompanionSettings companionSettings) => Results.Ok(new
{
    service = "LineCompanionBot",
    webhook = companionSettings.HasWebhook ? "enabled" : "disabled (set LINE_CHANNEL_SECRET)",
    messaging = companionSettings.HasMessaging ? "enabled" : "disabled (set LINE_CHANNEL_ACCESS_TOKEN)",
    shop = companionSettings.HasShop ? "enabled" : "disabled (set LINE_MINIAPP_LIFF_ID)",
}));

app.MapShopEndpoints();
app.MapWebhookEndpoint();

app.Run();
