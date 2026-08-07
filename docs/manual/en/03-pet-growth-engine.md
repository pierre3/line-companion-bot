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

**Design decisions, and why:**

- **Lazy decay, no background timer.** `ApplyDecay` computes hunger/happiness loss from elapsed
  wall-clock time whenever the pet is touched. A `BackgroundService` ticking to simulate decay
  would be simulation for its own sake — nothing observes the pet between interactions anyway.
- **No "death" mechanic.** `Play` while `Hunger <= 20` fails (`Success: false`) but loses nothing —
  the pet just needs feeding first. The demo should never permanently punish a user for not checking
  in; the failure branch exists to show off error/branch handling, not to make the game harsher.
- **Level from a formula, not a table.** `Level = 1 + Xp / 50`, plain integer division. Three stages
  are bucketed straight from the level.

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
PetState>` wrapping those two methods in `Task.FromResult`. Register it through a single seam —
create `Persistence/InMemory/PersistenceServiceCollectionExtensions.cs`:

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
[Fact] public void ApplyDecay_ReducesHungerAndHappinessProportionallyToElapsedTime() { /* 80 - 5*2 = 70 */ }
[Fact] public void ApplyDecay_ClampsAtZero_NeverGoesNegative() { /* stays 0 after 100h */ }
[Fact] public void Feed_IncreasesHunger_ClampedAt100_AndGrantsXp() { /* 90 + 30 → 100 */ }
[Fact] public void Play_FailsWhenTooHungry_AndDoesNotGrantXpOrHappiness() { /* Hunger 20 → refused */ }
[Fact] public void Play_SucceedsWhenNotTooHungry_IncreasesHappinessAndXp() { /* Hunger 21 → +25 */ }
[Fact] public void Play_IncreasesHappiness_ClampedAt100() { /* 90 + 25 → 100 */ }
[Fact] public void FeedRare_RefillsHungerToFull_RegardlessOfStartingValue() { /* 5 → 100 */ }
[Theory] public void Level_IsComputedFromXpWithoutATable(int xp, int expectedLevel) { /* 0→1, 50→2, 200→5 */ }
[Theory] public void Stage_MapsLevelToThreeBands(int xp, PetStage expectedStage) { /* boundaries */ }
```

Run the **test** task (or `dotnet test`) — all green. The `Play`-clamped-at-100 and `FeedRare`
cases were added after the fact when a review noticed the tutorial claimed coverage the tests
didn't actually have; the boundary rows (`Hunger 20` refuses, `21` succeeds) are where the hunger
gate is genuinely easy to get off-by-one. Nothing here talks to LINE yet — that's the next chapter.
