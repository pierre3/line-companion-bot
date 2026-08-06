**English** | [日本語](../ja/README.md)

# Tutorial: Building a Virtual Companion Bot + MINI App Shop

This is a hands-on walkthrough of how `LineCompanionBot` was built, from an empty directory to a
working app. It shows **how the `Line.OpenApi.*` packages wire together** into one realistic
system: a LINE bot where users raise a virtual pet via chat, plus a MINI App shop where they buy
items for it with In-App Purchase (IAP), with completed purchases announced back in chat.

The goal isn't to re-explain what each package's API does — see the concept articles in the
[`line-dotnet` manual](https://github.com/pierre3/line-openapi-dotnet) for that. The goal here is
the *integration*: the seams between Messaging, Webhook, and MINI App, and the design decisions
that make them fit together.

## How this tutorial is organized

You start from nothing — `dotnet new` scaffolds the solution — and build up one implementation
step per chapter. Each chapter ends with something you can run and observe in **Visual Studio
Code** (F5 to launch and debug), so you never go more than a few minutes without seeing the piece
you just wrote actually work. Chapters 1–8 are fully verifiable on your own machine with no LINE
account at all; only Chapter 9 needs a live channel.

The code shown in each chapter matches the repository's final state 1:1 — where a later design
pass changed something (configuration binding, the persistence abstraction, a review-gate fix),
that final form is what the chapter presents, not an older version you'd later have to unlearn.

## Chapters

| # | Chapter | What you build |
|---|---|---|
| — | [Getting started](00-getting-started.md) | Scaffold from scratch (`dotnet new`), VS Code + user-secrets, first F5 run |
| 1 | [Project skeleton and DI wiring](01-project-skeleton.md) | Config binding, gated DI, a health endpoint that never refuses to boot |
| 2 | [Webhook receive + signature verification](02-webhook.md) | `POST /webhook` — verify the HMAC signature, always ack 200 |
| 3 | [Pet state and the growth engine](03-pet-growth-engine.md) | The pure pet simulation, behind an `IPetStore` seam, unit-tested |
| 4 | [Flex replies and postback dispatch](04-flex-postback.md) | Status cards, and driving pet care from rich-menu postbacks |
| 5 | [Rich menu bootstrap](05-rich-menu.md) | A one-shot `setup` CLI verb that creates and activates the menu |
| 6 | [MINI App shop: front end and backend](06-shop.md) | The shop page, the reserve contract, and the IAP handoff |
| 7 | [Purchase reconciliation](07-reconciliation.md) | A polling `BackgroundService` that grants completed purchases |
| 8 | [Notifying the user](08-notify.md) | Service message, with a push fallback that's the default path |
| 9 | [End-to-end with a real channel](09-end-to-end.md) | Console setup, dev tunnel, the full loop, and troubleshooting |

## The two deliverables

This tutorial and the app are meant to be finished together: the app is the *what*, and this
manual is the *how*. Before the app was considered done it went through a 3-role review gate
(code / security / test-architecture); the findings that came out of it are folded into the
chapters they touch rather than bolted on at the end, so what you read is the reviewed, final
shape of each piece.
