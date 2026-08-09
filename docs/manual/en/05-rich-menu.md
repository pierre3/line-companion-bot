[← Chapter 4](04-flex-postback.md) | [Index](README.md) | [Chapter 6 →](06-shop.md)

# Chapter 5 — Registering the rich menu with the `line` tool

**What we're building:** not application code this time — a rich menu *definition* (`richmenu.json`)
that we register with LINE using the `Line.OpenApi.Tools` command-line tool. This is the piece that
turns Chapter 4's postback strings (`"action=feed"` etc.) into something a user can actually tap.

The `Line.OpenApi.*` family ships a command-line tool, `Line.OpenApi.Tools`, whose `richmenu`
commands manage rich menus end to end — create one from a definition, upload its image, set it as
the default, list or delete. We register the menu with it, so this step needs no app code.

## Install the tool

`Line.OpenApi.Tools` is a .NET global tool (command name `line`) that doubles as an MCP server:

```powershell
dotnet tool install -g Line.OpenApi.Tools --version 0.2.0-preview
```

`line --help` should now list the command groups (`richmenu`, `config`, …). (If you have the
`line-dotnet` source checked out, you can run it without installing:
`dotnet run --project path/to/line-dotnet/tools/Line.OpenApi.Tools -- <command>`.)

## The rich menu definition

`line richmenu create` takes a JSON definition — the standard LINE rich menu shape. Create
`src/LineCompanionBot/assets/richmenu.json`:

```json
{
  "size": { "width": 2500, "height": 1686 },
  "selected": true,
  "name": "LineCompanionBot default menu",
  "chatBarText": "Menu",
  "areas": [
    {
      "bounds": { "x": 0, "y": 0, "width": 1250, "height": 843 },
      "action": { "type": "postback", "data": "action=feed" }
    },
    {
      "bounds": { "x": 1250, "y": 0, "width": 1250, "height": 843 },
      "action": { "type": "postback", "data": "action=play" }
    },
    {
      "bounds": { "x": 0, "y": 843, "width": 1250, "height": 843 },
      "action": { "type": "postback", "data": "action=status" }
    },
    {
      "bounds": { "x": 1250, "y": 843, "width": 1250, "height": 843 },
      "action": { "type": "uri", "uri": "https://liff.line.me/YOUR_LIFF_ID" }
    }
  ]
}
```

Four tappable areas on a 2500×1686 canvas, split into quadrants:

- **Feed / Play / Status** are `postback` areas whose `data` is exactly the strings the webhook
  dispatches on in [Chapter 4](04-flex-postback.md) — this is where those strings finally get a
  sender. These `data` values and the `switch` cases in `WebhookEndpoints.cs` are matched by hand,
  so if you rename one, rename the other or the tap does nothing.
- **Shop** is a `uri` area that opens the MINI App's LIFF URL (it sends no postback; LINE just opens
  the URL). Replace `YOUR_LIFF_ID` with your MINI App's LIFF id — you'll have one after
  [Chapter 6](06-shop.md)/[Chapter 9](09-end-to-end.md).

## The image

`richmenu.json` describes the tappable regions; the menu also needs a background image — a real PNG
on disk, there's no way around uploading actual pixels. Put a `richmenu.png` in
`src/LineCompanionBot/assets/`. You can copy the placeholder from the reference repository's
[`src/LineCompanionBot/assets/richmenu.png`](https://github.com/pierre3/line-companion-bot/blob/main/src/LineCompanionBot/assets/richmenu.png),
or make your own: this project has no image-generation library (adding one to draw four boxes would
be a disproportionate dependency), so the placeholder was generated once, out-of-band, with a
throwaway PowerShell + `System.Drawing` script (a build-time artifact, not part of the app):

```powershell
Add-Type -AssemblyName System.Drawing
# ...draw four labeled 1250x843 quadrants (FEED / PLAY / STATUS / SHOP) on a 2500x1686 canvas...
$bmp.Save("assets/richmenu.png", [System.Drawing.Imaging.ImageFormat]::Png)
```

Replace it with real artwork before using the app for anything beyond a demo. Unlike an embedded
asset, the tool takes the image path explicitly (`--file`), so there's no `.csproj` copy step to
worry about.

## Register it

All three steps need a channel access token. Supply it whichever way suits you — an environment
variable (`LINE_CHANNEL_ACCESS_TOKEN`), a per-command `--channel-token`, or a saved profile
(`line config set default --token "..."`, stored in `~/.line/config.json`). Either way the token
lands in your shell environment or a plaintext file, so prefer one you can revoke and clear it when
you're done. Then, from the repo root:

```powershell
# 1. Create the menu from the definition — prints the new rich menu id.
line richmenu create --file src/LineCompanionBot/assets/richmenu.json

# 2. Upload the background image to that id.
line richmenu image <richMenuId> --file src/LineCompanionBot/assets/richmenu.png

# 3. Make it the default menu for every user of the channel.
line richmenu set-default <richMenuId>
```

`create` prints the `richMenuId` you paste into steps 2 and 3. The image upload goes to LINE's
*data* host (`api-data.line.me`), a different host from the control-plane calls — the tool routes it
for you, sparing you the BaseUrl split you'd otherwise set up by hand on the low-level client.

## The same tool as an MCP server (optional)

`line` doubles as an MCP server, so you can drive the same operations from Claude Code instead of
the shell:

```powershell
claude mcp add line -- line mcp
```

That exposes `line_richmenu_create`, `line_richmenu_set_default`, `line_richmenu_list`, and the rest
as MCP tools. One deliberate gap: **image upload is CLI-only** (shipping binary through MCP is
impractical), so even in an MCP-driven flow the `line richmenu image` step still runs via the CLI.

## Aside — let an AI agent author the definition and image (optional)

With an agent like Claude Code on hand you can go one step further: instead of hand-authoring
`richmenu.json` and `richmenu.png`, have the agent produce them. Keep the roles straight — **authoring
the JSON and image is the agent's own capability**, while the `line` MCP server only *registers*
(`line_richmenu_create` / `line_richmenu_set_default`). Three prompts carry it end to end.

**① Author the definition.** Always include the instruction to match Chapter 4's dispatch — miss this
and taps do nothing:

> Create `src/LineCompanionBot/assets/richmenu.json`. A 2500×1686 canvas split into four. Top-left =
> Feed, top-right = Play, bottom-left = Status are `postback` areas with `data` set to `action=feed`
> / `action=play` / `action=status` respectively (match the `switch` cases in `WebhookEndpoints.cs`).
> Bottom-right = Shop is a `uri` action to `https://liff.line.me/YOUR_LIFF_ID`. Set `name` to
> "LineCompanionBot default menu", `chatBarText` to "Menu", and `selected` to true.

**② Generate the placeholder image.** The same thing the chapter did with a throwaway System.Drawing
script — just asked for in words:

> Write and run a throwaway script that generates a 2500×1686 placeholder PNG at
> `src/LineCompanionBot/assets/richmenu.png`, matching the layout of the `richmenu.json` above. Tint
> the four quadrants faintly and draw FEED / PLAY / STATUS / SHOP large in the center (System.Drawing
> on Windows).

**③ Register it via MCP.** This one touches a real channel, so it needs a token and belongs to
Chapter 9:

> With the `line` MCP configured, go on to register it with `line_richmenu_create`. Image upload
> isn't in MCP, so run `line richmenu image <id> --file …` via the CLI, then set it as the default
> with `line_richmenu_set_default` (in that order: create → image → set-default).

Steps ① and ② are token-free, so you can try them now — but eyeball what comes out: that the postback
`data` matches the `case`s in `WebhookEndpoints.cs`, and that Shop's `uri` still carries the
`YOUR_LIFF_ID` placeholder (that the agent didn't invent a plausible-looking id — you fill the real
one in Chapter 9). Registration in ③ needs a real token exactly as the CLI steps do, so it's
[Chapter 9](09-end-to-end.md)'s job.

## Try it

Every `line richmenu` command calls LINE with your token, so there's nothing to run fully offline
here beyond `line --help` and eyeballing `richmenu.json`. Registering the menu for real — the three
commands above — belongs to [Chapter 9](09-end-to-end.md), where you wire up a real channel. If you
already have a channel access token, run them now and the menu (Feed / Play / Status / Shop) appears
the moment you add the bot as a friend.
