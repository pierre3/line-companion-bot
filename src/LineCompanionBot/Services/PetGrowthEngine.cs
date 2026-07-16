namespace LineCompanionBot.Services;

public enum PetStage
{
    Hatchling,
    Juvenile,
    Adult,
}

public sealed record PetState(string UserId, string Name, double Hunger, double Happiness, int Xp, DateTimeOffset LastInteractionUtc);

public sealed record PlayResult(PetState State, bool Success);

// Pure, dependency-free pet simulation logic. Deliberately anemic: the point being demonstrated
// is the postback -> state -> Flex reply plumbing, not game design depth. No background timer —
// decay is computed lazily from elapsed wall-clock time whenever the pet is touched.
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
        return decayed with
        {
            Hunger = Math.Min(100, decayed.Hunger + FeedHungerGain),
            Xp = decayed.Xp + XpPerAction,
        };
    }

    // The shop's "Golden Kibble" purchase (see ShopCatalog) — a full instant refill instead of the
    // usual partial gain. Consumed on use (InventoryStore.TryConsume), so this only ever applies once.
    public static PetState FeedRare(PetState state, DateTimeOffset now)
    {
        var decayed = ApplyDecay(state, now);
        return decayed with { Hunger = 100, Xp = decayed.Xp + XpPerAction };
    }

    // A pet that's too hungry refuses to play — a deliberate failure branch, not a "death"
    // mechanic: nothing is ever lost permanently, the pet just needs feeding first.
    public static PlayResult Play(PetState state, DateTimeOffset now)
    {
        var decayed = ApplyDecay(state, now);
        if (decayed.Hunger <= PlayHungerThreshold)
        {
            return new PlayResult(decayed, Success: false);
        }

        var played = decayed with
        {
            Happiness = Math.Min(100, decayed.Happiness + PlayHappinessGain),
            Xp = decayed.Xp + XpPerAction,
        };
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
