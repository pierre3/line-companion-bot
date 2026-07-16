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
