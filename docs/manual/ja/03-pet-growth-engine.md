[← 第2章](02-webhook.md) | [索引](README.md) | [第4章 →](04-flex-postback.md)

# 第3章 — Pet状態と成長エンジン

**このステップで作るもの:** ペットのシミュレーション本体——`PetState`・`PetGrowthEngine`・
それを保持する `IPetStore`——であり、**いかなるLINE APIにも依存しません**。そして実は、ここは
このアプリで唯一、意図的にユニットテストを書く価値がある部分でもあります。というのも、減衰クランプ・
レベル閾値・空腹ゲートといった本物のエッジケースを持つ純粋な分岐ロジックだからです——単体で検証する
コストは低く、それでいて重要なミスをしっかり捕まえられる、割のいい場所なのです。

## エンジン

`src/LineCompanionBot/Services/PetGrowthEngine.cs` を作成します:

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

**ペット育成の仕様**

相棒は2つの *欲求* と *経験値* を持ち、3つの操作で世話をします。恒久的に積み上がるのはXpだけで、これが
3段階のライフステージを通じた成長を駆動します。

欲求（いずれも `0〜100`、常にこの範囲に保たれる）:

| 欲求 | 時間経過 | 回復手段 |
|---|---|---|
| **Hunger（空腹度）** | 減っていく | **feed（餌やり）** |
| **Happiness（幸福度）** | 減っていく（Hungerより遅い） | **play（遊ぶ）** |

操作:

| 操作 | 効果 | Xp |
|---|---|---|
| **feed** | Hungerを回復 | + |
| **play** | Happinessを上げる（*空腹すぎると拒否*） | +（成功時のみ） |
| **status** | 現在の状態を表示・回復なし | — |

成長: Xp → **Level**（上がる一方の数値）→ **Stage**:

| Stage | Level |
|---|---|
| 🥚 Hatchling | 1 |
| 🐣 Juvenile | 2〜4 |
| 🐔 Adult | 5以上 |

ループの形を決めるルールが2つ:

- 空腹が過ぎるペットは、餌をやるまで **遊びを拒否** します——その試みは何も起きないだけで、「死亡」や
  恒久的な喪失はありません。
- ショップの **レア餌はHungerを一度で満タンまで回復** させます（通常の餌やりのわずかな回復とは違います）。

**各メソッドが受け持つ役割:**

- **`ApplyDecay`** — 時間経過を精算する唯一の場所。ある時点までペットの欲求を最新化します。他の
  アクションはすべて先頭でこれを呼ぶので、バックグラウンドタイマーは不要です。
- **`Feed`** / **`FeedRare`** — 餌やりアクション。`Feed` は通常の補充、`FeedRare` はショップの
  Golden Kibble（一度きりの全回復）です。
- **`Play`** — 遊ぶアクション。空腹すぎるときの拒否を含み、`PlayResult` が成功可否を呼び出し元に
  伝えるので、応答を出し分けられます。
- **`Status`** — 様子見アクション。欲求の最新化以外に変化はありません。
- **`Level`** / **`Stage`** — Xpから導く成長の読み出し。数値のレベルと、カードに表示する3段階の
  ライフステージです。

## インターフェース越しのストア

今日のところ唯一の実装がインメモリのdictionaryだとしても、各ストアは `Persistence/` 配下の
インターフェース越しに公開しておきます——将来データベースを、呼び出し元に一切触れることなく差し込める
ようにするための継ぎ目（seam）です。まずは `src/LineCompanionBot/Persistence/IPetStore.cs` を作成します:

```csharp
using LineCompanionBot.Services;

namespace LineCompanionBot.Persistence;

public interface IPetStore
{
    Task<PetState> GetOrCreateAsync(string userId, DateTimeOffset now, CancellationToken ct = default);
    Task SaveAsync(PetState state, CancellationToken ct = default);
}
```

各メソッドは、インメモリ実装が実際には一度もawaitしないにもかかわらず、あえて**async形**
（`CancellationToken` を伴う `Task`/`Task<T>`）にしてあります。理由はこうです——インターフェースと
いうものは、後から `CancellationToken` を足したり、同期を非同期に変えたりを、すべての呼び出し箇所に
手を入れずに済ませることができません。ならば最初から実I/Oを想定した形にしておけば、いざEF Core /
Dapper 実装を差し込むときも、呼び出し元には一切手を入れずに済むわけです。

`InMemoryPetStore`（`Persistence/InMemory/InMemoryPetStore.cs`）の実体は、その2メソッドを
`Task.FromResult` でラップしただけの `ConcurrentDictionary<string, PetState>` です。まず
`Persistence/InMemory/InMemoryPetStore.cs` を作成します:

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

登録もまた、たった1つの継ぎ目を通して行います——`Persistence/InMemory/PersistenceServiceCollectionExtensions.cs`
を作成します:

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

あとは `Program.cs` で `builder.Services.AddInMemoryPersistence();` を呼び出すだけです。本番デプロイに
移すときも、差し替えるのはこの1行——たとえば `AddSqlPersistence(connectionString)` に置き換えるだけで
済みます。というのも、すべての消費者は `I*Store` インターフェースにだけ依存していて、具象型には一切
触れていないからです。（ここでSingletonを選んでいるのは、dictionaryが単一のリクエストより長生きしなければ
ならないため。このライフタイムの選択が、バックグラウンドサービスからのストア解決の仕方をどう形作って
いくかは、[第7章](07-reconciliation.md)で改めて説明します。）

## 試してみる

`tests/LineCompanionBot.Tests/PetGrowthEngineTests.cs` に、エッジケースを網羅するテストを追加します:

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

**test** タスク（または `dotnet test`）を実行してみてください——すべてグリーンになるはずです。`Play` の
100クランプと `FeedRare` のケースは、実は後から追加したものです。チュートリアルが、テストには実際には
存在しないカバレッジを謳っている——そのことにレビューが気づいたのがきっかけでした。そして境界の行
（`Hunger 20` は拒否、`21` は成功）ですが、空腹ゲートというのはまさにoff-by-oneを起こしやすい箇所なの
です。さて、ここまではまだLINEとは一言も通信していません——それは次の章の仕事です。
