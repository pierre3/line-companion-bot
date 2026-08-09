using Line.OpenApi.Messaging.DependencyInjection;
using Line.OpenApi.Messaging.Webhook.DependencyInjection;
using Line.OpenApi.MiniApp.DependencyInjection;
using LineCompanionBot;
using LineCompanionBot.Endpoints;
using LineCompanionBot.Persistence.InMemory;
using LineCompanionBot.Services;
using Microsoft.Extensions.Configuration;

// CompanionSettings is bound from its own configuration source — appsettings.json +
// appsettings.{Environment}.json + user secrets (Development only) + environment variables —
// deliberately built without AddCommandLine(), unlike the web host's own builder.Configuration
// (which includes it by default). LINE_CHANNEL_SECRET/LINE_CHANNEL_ACCESS_TOKEN are
// security-sensitive: they come from user secrets in development or from environment variables,
// never from the command line — letting a stray "--LINE_CHANNEL_SECRET=" argv entry silently win
// would be a regression from that. The web host below uses this helper instead of
// builder.Configuration so that contract holds.
static IConfiguration BuildCompanionConfiguration(string environmentName)
{
    var configurationBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environmentName}.json", optional: true);

    // User secrets (`dotnet user-secrets set LINE_CHANNEL_SECRET ...`) are the framework-recommended
    // local store for the LINE_* secrets in development — added here so the web host picks them up,
    // mirroring WebApplication.CreateBuilder's own Development-only user-secrets behavior but on this
    // app's dedicated configuration. Placed before the environment-variable provider so an explicit
    // env var still wins (standard .NET provider precedence).
    if (string.Equals(environmentName, "Development", StringComparison.Ordinal))
    {
        configurationBuilder.AddUserSecrets(typeof(Program).Assembly, optional: true);
    }

    return configurationBuilder
        .AddEnvironmentVariables()
        .Build();
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
