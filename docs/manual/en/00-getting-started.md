[← Index](README.md) | [Chapter 1 →](01-project-skeleton.md)

# Getting started — scaffolding from scratch

Before writing any LINE-specific code, this chapter gets you from an empty folder to a running,
debuggable ASP.NET Core app in Visual Studio Code. Nothing here is specific to `Line.OpenApi.*`
yet — it's the plain .NET project shape every later chapter builds on.

## Prerequisites

- **.NET 10 SDK** — check with `dotnet --version` (should print `10.*`).
- **Visual Studio Code** with the **C# Dev Kit** extension (`ms-dotnettools.csdevkit`). It brings
  the debugger, test runner, and solution view this tutorial assumes. The recommended
  `.vscode/extensions.json` you'll add below makes VS Code prompt you to install it when you open
  the folder.
- A LINE Messaging API channel and a MINI App channel are **not** needed until [Chapter 9](09-end-to-end.md).
  Everything before that runs with no LINE account.

> **Optional — go live early.** This tutorial is offline-first: Chapters 1–8 verify locally with no
> LINE account. If you'd rather see replies and the rich menu on your own phone *as* you build, set
> up a Messaging API channel + access token and a dev tunnel up front (the console and tunnel steps
> are in [Chapter 9](09-end-to-end.md)) and point the channel's webhook at your tunnel from Chapter 2
> on. Two caveats: the shop/IAP half still needs a review-gated MINI App channel (Chapters 6/9), and
> if you pause on a breakpoint the ~1-minute reply token can expire before the card is sent.

## Create the solution and projects

From the directory that will hold the repo:

```powershell
dotnet new sln -n LineCompanionBot

# The web app (SDK: Microsoft.NET.Sdk.Web), targeting net10.0.
dotnet new web -o src/LineCompanionBot -f net10.0

# A test project — Chapter 3 is the one piece of this app worth unit-testing.
dotnet new xunit -o tests/LineCompanionBot.Tests -f net10.0

dotnet sln add src/LineCompanionBot tests/LineCompanionBot.Tests
dotnet add tests/LineCompanionBot.Tests reference src/LineCompanionBot
```

`dotnet new web` gives you the smallest ASP.NET Core template — a one-line `Program.cs` that
returns "Hello World!". We'll replace it in Chapter 1; for now it's a known-good starting point.

Set `Nullable` and `ImplicitUsings` on the app project (both later chapters rely on) by editing
`src/LineCompanionBot/LineCompanionBot.csproj`:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```

## Add the LINE packages

Three of the `Line.OpenApi.*` packages are consumed here — as NuGet **PackageReferences**, the way
a real consumer would use them (this repo is deliberately independent of the library's own source
tree):

```powershell
dotnet add src/LineCompanionBot package Line.OpenApi.Messaging --version 1.0.0
dotnet add src/LineCompanionBot package Line.OpenApi.Messaging.Webhook --version 1.0.0
dotnet add src/LineCompanionBot package Line.OpenApi.MiniApp --version 1.0.0
```

`Line.OpenApi.Liff` is intentionally *not* referenced — this app never calls it, and a dependency
you don't call is just noise. `Messaging` covers reply/push/rich-menu, `Messaging.Webhook` covers
signature verification and payload parsing, and `MiniApp` covers the shop's reserve/notifier/IAP
polling.

## Open in VS Code and set up run/debug

Open the folder (`code .`). Create a `.vscode/` folder and add the three files that drive the F5
experience — copy `launch.json`, `tasks.json`, and `extensions.json` from the reference
repository's [`.vscode/`](https://github.com/pierre3/line-companion-bot/tree/main/.vscode)
directory into your project's `.vscode/`:

- **`launch.json`** — a single "Run LineCompanionBot" configuration. It builds first (`preLaunchTask`),
  launches the app's DLL with the debugger attached, sets `ASPNETCORE_ENVIRONMENT=Development`, and
  runs with the project folder as the working directory so `appsettings.json` resolves by its
  relative path.
- **`tasks.json`** — `build` and `test` tasks. (Chapter 5 registers the rich menu with the separate
  `line` global tool, not a VS Code task.)
- **`extensions.json`** — recommends the C# Dev Kit.

## Secrets: use `dotnet user-secrets`, not a checked-in file

The LINE channel secret and access token are sensitive and must never land in `appsettings.json`
or `launch.json`. The framework-recommended local store for them is **user secrets** — an
`secrets.json` kept outside the repo tree, keyed to the project by a `UserSecretsId`.

Enable it once:

```powershell
dotnet user-secrets init --project src/LineCompanionBot
```

That writes a `<UserSecretsId>` (a generated GUID) into the `.csproj` — the exact value is just a
stable identifier, so it doesn't matter that this repo happens to use a readable one. You won't set
any actual secrets until
[Chapter 9](09-end-to-end.md) (nothing before it needs a real token), but wiring the mechanism now
means later chapters can say "put it in user-secrets" without ceremony. Chapter 1 shows the one
line of `Program.cs` that makes `CompanionSettings` read from this store.

## First run

Press **F5**. VS Code builds, launches, and (via `serverReadyAction`) opens a browser at the
listening URL. The default template responds with `Hello World!` at `http://localhost:5091/`.
Set a breakpoint in `Program.cs`, refresh, and confirm the debugger stops — that's the whole
inner loop you'll use for every chapter from here.

Stop the app (the red square, or `Shift+F5`). Next chapter replaces this Hello-World skeleton with
the real configuration-and-DI shape.
