[← Chapter 2](02-webhook.md) | [Index](README.md) | [Chapter 4 →](04-flex-postback.md)

# Chapter 3 — Pet state and the growth engine

**What we're building:** the pet simulation itself — `PetState`, `PetGrowthEngine`, and an
`IPetStore` to hold it — with **no dependency on any LINE API**. This is deliberately the one piece
of the app worth unit-testing: it's pure branching logic (decay clamping, level thresholds, a hunger
gate) with real edge cases, so verifying it in isolation is cheap and catches the mistakes that
matter.

## The engine

Create `src/LineCompanionBot/Services/PetGrowthEngine.cs`:

```csharp
namespace LineCompanionBot.Services;

public enum PetStage { Hatchling, Juvenile, Adult }

public sealed record PetState(string UserId, string Name, double Hunger, double Happiness, int Xp, DateTimeOffset LastInteractionUtc);
public sealed record PlayResult(PetState State, bool Success);

public static class PetGrowthEngine
{
    public const double HungerDecayPerHour = 2.0;
    public const double HappinessDecayPerHour = 1.0;
    public const double FeedHungerGain = 30.0;
    public const double PlayHappinessGain = 25.0;
    public const double PlayHungerThreshold = 20.0;
    public const int XpPerAction = 5;
    public const int XpPerLevel = 50;

    public static PetState ApplyDecay(PetState state, DateTimeOffset now)
    {
        var elapsedHours = Math.Max(0, (now - state.LastInteractionUtc).TotalHours);
        var hunger = Math.Max(0, state.Hunger - elapsedHours * HungerDecayPerHour);
        var happiness = Math.Max(0, state.Happiness - elapsedHours * HappinessDecayPerHour);
        return state with { Hunger = hunger, Happiness = happiness, LastInteractionUtc = now };
    }

    public static PetState Feed(PetState state, DateTimeOffset now)
    {
        var decayed = ApplyDecay(state, now);
        return decayed with { Hunger = Math.Min(100, decayed.Hunger + FeedHungerGain), Xp = decayed.Xp + XpPerAction };
    }

    // The shop's "Golden Kibble" (Chapter 6): a full instant refill instead of the usual partial
    // gain. Consumed on use, so it only ever applies once.
    public static PetState FeedRare(PetState state, DateTimeOffset now)
    {
        var decayed = ApplyDecay(state, now);
        return decayed with { Hunger = 100, Xp = decayed.Xp + XpPerAction };
    }

    public static PlayResult Play(PetState state, DateTimeOffset now)
    {
        var decayed = ApplyDecay(state, now);
        if (decayed.Hunger <= PlayHungerThreshold)
            return new PlayResult(decayed, Success: false); // too hungry to play

        var played = decayed with { Happiness = Math.Min(100, decayed.Happiness + PlayHappinessGain), Xp = decayed.Xp + XpPerAction };
        return new PlayResult(played, Success: true);
    }

    public static PetState Status(PetState state, DateTimeOffset now) => ApplyDecay(state, now);

    public static int Level(PetState state) => 1 + state.Xp / XpPerLevel;

    public static PetStage Stage(PetState state) => Level(state) switch
    {
        1 => PetStage.Hatchling,
        >= 2 and <= 4 => PetStage.Juvenile,
        _ => PetStage.Adult,
    };
}
```

**How raising the pet works**

The companion tracks two *needs* and an *experience* score, and you care for it with three actions.
Only Xp accumulates permanently — it drives growth through three life stages.

Needs (both `0–100`, always kept in range):

| Need | Over time | Restored by |
|---|---|---|
| **Hunger** | drains | **feed** |
| **Happiness** | drains, slower than Hunger | **play** |

Actions:

| Action | Effect | Xp |
|---|---|---|
| **feed** | restores Hunger | + |
| **play** | raises Happiness — *refused while too hungry* | + (only on success) |
| **status** | shows the current state; no restore | — |

Growth: Xp → **Level** (a number that only rises) → **Stage**:

| Stage | Level |
|---|---|
| 🥚 Hatchling | 1 |
| 🐣 Juvenile | 2–4 |
| 🐔 Adult | 5+ |

Two rules give the loop its shape:

- A **too-hungry pet refuses to play** until it's fed — the attempt simply does nothing. There is no
  "death" or permanent loss.
- The shop's **rare food refills Hunger to full** in one go, unlike the small top-up ordinary
  feeding gives.

**What each method is responsible for:**

- **`ApplyDecay`** — the one place elapsed time is accounted for, bringing a pet's needs up to date
  as of a given moment. Every other action runs it first, which is why no background timer is needed.
- **`Feed`** / **`FeedRare`** — the feed action. `Feed` is the ordinary top-up; `FeedRare` is the
  shop's Golden Kibble, a one-shot full refill.
- **`Play`** — the play action, including the too-hungry refusal. Its `PlayResult` tells the caller
  whether the play succeeded, so the reply can differ.
- **`Status`** — the check-in action: nothing changes beyond bringing needs up to date.
- **`Level`** / **`Stage`** — the growth read-outs derived from Xp: the numeric level, and the
  three-band life stage shown on the card.

## The store, behind an interface

Even though the only implementation today is an in-memory dictionary, each store sits behind an
interface under `Persistence/` — the seam a future database plugs into without touching any caller.
Create `src/LineCompanionBot/Persistence/IPetStore.cs`:

```csharp
using LineCompanionBot.Services;

namespace LineCompanionBot.Persistence;

public interface IPetStore
{
    Task<PetState> GetOrCreateAsync(string userId, DateTimeOffset now, CancellationToken ct = default);
    Task SaveAsync(PetState state, CancellationToken ct = default);
}
```

The methods are **async-shaped** (`Task`/`Task<T>` with a `CancellationToken`) even though the
in-memory implementation never actually awaits — because an interface can't cheaply grow a
`CancellationToken` or turn sync into async later without touching every call site. Shaping it for
real I/O from the start means an EF Core / Dapper implementation drops in with no caller changes.

`InMemoryPetStore` (`Persistence/InMemory/InMemoryPetStore.cs`) is a `ConcurrentDictionary<string,
PetState>` wrapping those two methods in `Task.FromResult`:

```csharp
using System.Collections.Concurrent;
using LineCompanionBot.Services;

namespace LineCompanionBot.Persistence.InMemory;

// In-memory only, no persistence — the app is a demo, not a game server. State resets on restart.
// Swap the DI registration for a real IPetStore implementation to persist across restarts.
public sealed class InMemoryPetStore : IPetStore
{
    private const string DefaultName = "Pico";
    private const double InitialHunger = 80.0;
    private const double InitialHappiness = 80.0;

    private readonly ConcurrentDictionary<string, PetState> _pets = new();

    public Task<PetState> GetOrCreateAsync(string userId, DateTimeOffset now, CancellationToken ct = default)
        => Task.FromResult(_pets.GetOrAdd(userId, id => new PetState(id, DefaultName, InitialHunger, InitialHappiness, Xp: 0, now)));

    public Task SaveAsync(PetState state, CancellationToken ct = default)
    {
        _pets[state.UserId] = state;
        return Task.CompletedTask;
    }
}
```

Register it through a single seam — create
`Persistence/InMemory/PersistenceServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace LineCompanionBot.Persistence.InMemory;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IPetStore, InMemoryPetStore>();
        // Chapter 6 adds IInventoryStore, IOrderStore, and INotifierTokenStore here.
        return services;
    }
}
```

and call `builder.Services.AddInMemoryPersistence();` in `Program.cs`. A real deployment swaps that
one line for, e.g., `AddSqlPersistence(connectionString)` — every consumer depends on the `I*Store`
interfaces, never on the concrete types. (Singleton here because the dictionaries must outlive any
single request; [Chapter 7](07-reconciliation.md) explains why that lifetime choice shapes how the
background service resolves stores.)

## Try it

Add tests to `tests/LineCompanionBot.Tests/PetGrowthEngineTests.cs` covering the edge cases:

```csharp
using LineCompanionBot.Services;
using Xunit;

namespace LineCompanionBot.Tests;

public class PetGrowthEngineTests
{
    private static PetState NewState(double hunger = 80, double happiness = 80, int xp = 0, DateTimeOffset? lastInteraction = null)
        => new("user-1", "Pico", hunger, happiness, xp, lastInteraction ?? DateTimeOffset.UtcNow);

    [Fact]
    public void ApplyDecay_ReducesHungerAndHappinessProportionallyToElapsedTime()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var state = NewState(hunger: 80, happiness: 80, lastInteraction: start);

        var decayed = PetGrowthEngine.ApplyDecay(state, start.AddHours(5));

        Assert.Equal(70, decayed.Hunger);   // 80 - 5*2
        Assert.Equal(75, decayed.Happiness); // 80 - 5*1
    }

    [Fact]
    public void ApplyDecay_ClampsAtZero_NeverGoesNegative()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var state = NewState(hunger: 5, happiness: 5, lastInteraction: start);

        var decayed = PetGrowthEngine.ApplyDecay(state, start.AddHours(100));

        Assert.Equal(0, decayed.Hunger);
        Assert.Equal(0, decayed.Happiness);
    }

    [Fact]
    public void Feed_IncreasesHunger_ClampedAt100_AndGrantsXp()
    {
        var now = DateTimeOffset.UtcNow;
        var state = NewState(hunger: 90, xp: 0, lastInteraction: now);

        var fed = PetGrowthEngine.Feed(state, now);

        Assert.Equal(100, fed.Hunger); // 90 + 30 clamped to 100
        Assert.Equal(PetGrowthEngine.XpPerAction, fed.Xp);
    }

    [Fact]
    public void Play_FailsWhenTooHungry_AndDoesNotGrantXpOrHappiness()
    {
        var now = DateTimeOffset.UtcNow;
        var state = NewState(hunger: 20, happiness: 50, xp: 0, lastInteraction: now);

        var result = PetGrowthEngine.Play(state, now);

        Assert.False(result.Success);
        Assert.Equal(50, result.State.Happiness);
        Assert.Equal(0, result.State.Xp);
    }

    [Fact]
    public void Play_SucceedsWhenNotTooHungry_IncreasesHappinessAndXp()
    {
        var now = DateTimeOffset.UtcNow;
        var state = NewState(hunger: 21, happiness: 50, xp: 0, lastInteraction: now);

        var result = PetGrowthEngine.Play(state, now);

        Assert.True(result.Success);
        Assert.Equal(75, result.State.Happiness); // 50 + 25
        Assert.Equal(PetGrowthEngine.XpPerAction, result.State.Xp);
    }

    [Fact]
    public void Play_IncreasesHappiness_ClampedAt100()
    {
        var now = DateTimeOffset.UtcNow;
        var state = NewState(hunger: 21, happiness: 90, xp: 0, lastInteraction: now);

        var result = PetGrowthEngine.Play(state, now);

        Assert.True(result.Success);
        Assert.Equal(100, result.State.Happiness); // 90 + 25 clamped to 100
    }

    [Fact]
    public void FeedRare_RefillsHungerToFull_RegardlessOfStartingValue()
    {
        var now = DateTimeOffset.UtcNow;
        var state = NewState(hunger: 5, xp: 0, lastInteraction: now);

        var fed = PetGrowthEngine.FeedRare(state, now);

        Assert.Equal(100, fed.Hunger);
        Assert.Equal(PetGrowthEngine.XpPerAction, fed.Xp);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(49, 1)]
    [InlineData(50, 2)]
    [InlineData(199, 4)]
    [InlineData(200, 5)]
    [InlineData(1000, 21)]
    public void Level_IsComputedFromXpWithoutATable(int xp, int expectedLevel)
    {
        var state = NewState(xp: xp);

        Assert.Equal(expectedLevel, PetGrowthEngine.Level(state));
    }

    [Theory]
    [InlineData(0, PetStage.Hatchling)]
    [InlineData(49, PetStage.Hatchling)]
    [InlineData(50, PetStage.Juvenile)]
    [InlineData(199, PetStage.Juvenile)]
    [InlineData(200, PetStage.Adult)]
    public void Stage_MapsLevelToThreeBands(int xp, PetStage expectedStage)
    {
        var state = NewState(xp: xp);

        Assert.Equal(expectedStage, PetGrowthEngine.Stage(state));
    }
}
```

Run the **test** task (or `dotnet test`) — all green. The `Play`-clamped-at-100 and `FeedRare`
cases were added after the fact when a review noticed the tutorial claimed coverage the tests
didn't actually have; the boundary rows (`Hunger 20` refuses, `21` succeeds) are where the hunger
gate is genuinely easy to get off-by-one. Nothing here talks to LINE yet — that's the next chapter.
