using Line.OpenApi.Messaging;
using Line.OpenApi.Messaging.Generated.Api.Models;

namespace LineCompanionBot.Services;

// One-shot CLI bootstrap ("dotnet run -- setup"), not an HTTP endpoint: creating/replacing the
// account-wide default rich menu is an admin action that shouldn't be reachable over a dev tunnel.
public static class RichMenuBootstrapper
{
    private const int MenuWidth = 2500;
    private const int MenuHeight = 1686;
    private const int HalfWidth = MenuWidth / 2;
    private const int HalfHeight = MenuHeight / 2;

    public static async Task RunAsync(CompanionSettings settings, string imagePath)
    {
        if (!settings.HasMessaging)
        {
            Console.Error.WriteLine("LINE_CHANNEL_ACCESS_TOKEN is not set — cannot create a rich menu.");
            return;
        }

        if (!settings.HasShop)
        {
            Console.WriteLine("Warning: LINE_MINIAPP_LIFF_ID is not set — the Shop button will link to a placeholder URL.");
        }

        var shopUri = settings.HasShop ? $"https://liff.line.me/{settings.LiffId}" : "https://line.me/";

        var request = new RichMenuRequest
        {
            Name = "LineCompanionBot default menu",
            ChatBarText = "Menu",
            Selected = true,
            Size = new RichMenuSize { Width = MenuWidth, Height = MenuHeight },
            Areas = new List<RichMenuArea>
            {
                Area(0, 0, "action=feed"),
                Area(HalfWidth, 0, "action=play"),
                Area(0, HalfHeight, "action=status"),
                AreaUri(HalfWidth, HalfHeight, shopUri),
            },
        };

        var richMenu = RichMenuClient.CreateWithStaticToken(settings.ChannelAccessToken!);
        var richMenuId = await richMenu.CreateAsync(request);
        if (richMenuId is null)
        {
            Console.Error.WriteLine("Failed to create the rich menu (no id returned).");
            return;
        }

        await richMenu.SetImageFromFileAsync(richMenuId, imagePath);
        await richMenu.SetDefaultAsync(richMenuId);

        Console.WriteLine($"Rich menu created and set as default: {richMenuId}");
    }

    private static RichMenuArea Area(int x, int y, string postbackData) => new()
    {
        Bounds = new RichMenuBounds { X = x, Y = y, Width = HalfWidth, Height = HalfHeight },
        Action = new PostbackAction { Data = postbackData },
    };

    private static RichMenuArea AreaUri(int x, int y, string uri) => new()
    {
        Bounds = new RichMenuBounds { X = x, Y = y, Width = HalfWidth, Height = HalfHeight },
        Action = new URIAction { Uri = uri },
    };
}
