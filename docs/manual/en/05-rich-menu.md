[← Chapter 4](04-flex-postback.md) | [Index](README.md) | [Chapter 6 →](06-shop.md)

# Chapter 5 — Rich menu bootstrap (`dotnet run -- setup`)

**What we're building:** a one-shot CLI verb that creates the rich menu, uploads its image, and sets
it as the account's default — the piece that turns the postback strings from Chapter 4
(`"action=feed"` etc.) into something a user can actually tap.

**Why a CLI verb, not an HTTP endpoint.** Setting the *default* rich menu is account-wide — it
affects every user of the channel. That's an admin action, and this app is exposed to the internet
over a dev tunnel for the webhook to work. A `POST /setup` endpoint would put a destructive,
unauthenticated admin action on the same public surface as the webhook. Dispatching on `args[0]`
*before* `WebApplication` is even built keeps it strictly local.

## The setup verb in Program.cs

At the very top of `Program.cs`, before `WebApplication.CreateBuilder`, add the verb dispatch (it
reuses the same `BuildCompanionConfiguration` helper from Chapter 1, so user-secrets and env vars
resolve identically to the web host):

```csharp
if (args.Length > 0 && args[0] == "setup")
{
    var setupEnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
    var setupSettings = BuildCompanionConfiguration(setupEnvironmentName).Get<CompanionSettings>() ?? new CompanionSettings();
    await RichMenuBootstrapper.RunAsync(setupSettings, "assets/richmenu.png");
    return;
}
```

Because this path has no host, it has no ambient `IConfiguration` to reuse — building its own via
the shared helper is exactly why that helper exists. Note the environment name comes from
`ASPNETCORE_ENVIRONMENT`; the `setup-richmenu` task in `.vscode/tasks.json` sets it to `Development`
so your user-secrets token is picked up.

## The bootstrapper

Create `src/LineCompanionBot/Services/RichMenuBootstrapper.cs`:

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

`Area` / `AreaUri` are small helpers building the four quadrant `RichMenuArea`s. Three use
`PostbackAction` (matching the `"action=..."` strings the webhook already dispatches on); the
fourth uses `URIAction` pointing at the MINI App shop's LIFF URL — the shop button needs no
postback, LINE just opens the URL.

`RichMenuClient.CreateWithStaticToken(...)` is the facade for the rich-menu endpoints. It matters
here specifically because uploading the image hits LINE's *data* host (`api-data.line.me`), which
must be configured before the client is built — the facade handles that internally, so you don't
have to think about the BaseUrl ordering the low-level `MessagingClient.Blob` would require.

## The blocking prerequisite: an actual image file

`SetImageFromFileAsync` needs a real PNG on disk — there's no way around uploading actual pixels.
This repo has no image-generation library (adding one to draw four boxes would be a
disproportionate dependency), so the placeholder at `assets/richmenu.png` was generated once,
out-of-band, with a throwaway PowerShell + `System.Drawing` script (a build-time artifact, not part
of the app):

```powershell
Add-Type -AssemblyName System.Drawing
# ...draw four labeled 1250x843 quadrants (FEED / PLAY / STATUS / SHOP) on a 2500x1686 canvas...
$bmp.Save("assets/richmenu.png", [System.Drawing.Imaging.ImageFormat]::Png)
```

Replace this file with real artwork before using the app for anything beyond a demo. Make sure the
`.csproj` copies it to the output (or the relative path `"assets/richmenu.png"` won't resolve when
run from the build directory).

## Try it — no real channel needed to confirm the wiring

Run the **setup-richmenu** task (VS Code: *Terminal → Run Task → setup-richmenu*), or from a
terminal:

```powershell
dotnet run --project src/LineCompanionBot -- setup
```

With no token configured:

```
LINE_CHANNEL_ACCESS_TOKEN is not set — cannot create a rich menu.
```

The verb dispatched before the web host started and exited cleanly — no server booted, no crash.
Running it against a real channel access token (Chapter 9) actually creates and activates the menu.
