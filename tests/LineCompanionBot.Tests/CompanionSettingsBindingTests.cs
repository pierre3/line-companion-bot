using Microsoft.Extensions.Configuration;
using Xunit;

namespace LineCompanionBot.Tests;

public class CompanionSettingsBindingTests
{
    // Mirrors Program.cs exactly: Get&lt;T&gt;() returns null (not a defaulted instance) when the
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
