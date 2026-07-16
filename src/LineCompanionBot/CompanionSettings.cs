namespace LineCompanionBot;

// Read once from environment variables at startup (no appsettings.json binding — matches the
// Line.OpenApi.* sample convention of plain env-var configuration, always-startable app).
public sealed record CompanionSettings(
    string? ChannelSecret,
    string? ChannelAccessToken,
    string? LiffId,
    string? TemplateName,
    int PollSeconds)
{
    public bool HasWebhook => !string.IsNullOrWhiteSpace(ChannelSecret);
    public bool HasMessaging => !string.IsNullOrWhiteSpace(ChannelAccessToken);
    public bool HasShop => !string.IsNullOrWhiteSpace(LiffId);

    public static CompanionSettings FromEnvironment()
    {
        var pollSecondsRaw = Environment.GetEnvironmentVariable("LINE_MINIAPP_POLL_SECONDS");
        var pollSeconds = int.TryParse(pollSecondsRaw, out var parsed) && parsed > 0 ? parsed : 30;

        return new CompanionSettings(
            ChannelSecret: Environment.GetEnvironmentVariable("LINE_CHANNEL_SECRET"),
            ChannelAccessToken: Environment.GetEnvironmentVariable("LINE_CHANNEL_ACCESS_TOKEN"),
            LiffId: Environment.GetEnvironmentVariable("LINE_MINIAPP_LIFF_ID"),
            TemplateName: Environment.GetEnvironmentVariable("LINE_MINIAPP_TEMPLATE_NAME"),
            PollSeconds: pollSeconds);
    }
}
