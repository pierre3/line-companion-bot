using Microsoft.Extensions.Configuration;

namespace LineCompanionBot;

// Bound from IConfiguration (env vars, appsettings.json, etc. — see Program.cs) via
// configuration.Get<CompanionSettings>(), the standard .NET configuration-binding mechanism.
// [ConfigurationKeyName] keeps every property bound to its original flat LINE_* key regardless of
// the C# property name, so this still reads from plain environment variables exactly as before —
// no appsettings.json section, no renamed env vars.
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

    // Non-positive values (e.g. a "0" or negative typo) fall back to 30 rather than binding
    // successfully — PurchaseReconciliationService.ExecuteAsync constructs a PeriodicTimer from
    // this value outside its own poll-failure try/catch, and PeriodicTimer throws on a non-positive
    // interval, which would otherwise crash the whole host (BackgroundService's default exception
    // behavior stops the host) instead of merely leaving reconciliation misconfigured.
    [ConfigurationKeyName("LINE_MINIAPP_POLL_SECONDS")]
    public int PollSeconds
    {
        get => _pollSeconds;
        set => _pollSeconds = value > 0 ? value : 30;
    }

    public bool HasWebhook => !string.IsNullOrWhiteSpace(ChannelSecret);
    public bool HasMessaging => !string.IsNullOrWhiteSpace(ChannelAccessToken);
    public bool HasShop => !string.IsNullOrWhiteSpace(LiffId);
}
