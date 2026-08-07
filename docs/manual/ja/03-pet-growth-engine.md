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

**設計判断とその理由:**

- **遅延減衰、バックグラウンドタイマーなし。** `ApplyDecay` は、ペットに触れるたびに、経過した実時間
  （wall-clock time）からHunger/Happinessの減り具合を計算します。減衰をシミュレートするためだけに
  tickする `BackgroundService` を置くこともできますが、それはシミュレーションのためのシミュレーション
  になってしまうでしょう——そもそも、操作の合間にペットをじっと観測している人はいないのですから。
- **「死亡」メカニクスなし。** `Hunger <= 20` の状態での `Play` は失敗します（`Success: false`）が、
  それで何かを失うわけではありません——ペットはまず餌やりが必要なだけです。デモがチェックインを怠った
  ユーザーを恒久的に罰する、という設計は絶対に採りません。この失敗分岐は、あくまでエラー/分岐処理の
  見せ場として存在するのであって、ゲームを厳しくするためのものではないのです。
- **レベルはテーブルではなく数式から。** `Level = 1 + Xp / 50`——なんのことはない、ただの整数除算です。
  3つの段階も、そのレベルからそのまま区分けされます。

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
`Task.FromResult` でラップしただけの `ConcurrentDictionary<string, PetState>` です。登録もまた、
たった1つの継ぎ目を通して行います——`Persistence/InMemory/PersistenceServiceCollectionExtensions.cs`
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

**test** タスク（または `dotnet test`）を実行してみてください——すべてグリーンになるはずです。`Play` の
100クランプと `FeedRare` のケースは、実は後から追加したものです。チュートリアルが、テストには実際には
存在しないカバレッジを謳っている——そのことにレビューが気づいたのがきっかけでした。そして境界の行
（`Hunger 20` は拒否、`21` は成功）ですが、空腹ゲートというのはまさにoff-by-oneを起こしやすい箇所なの
です。さて、ここまではまだLINEとは一言も通信していません——それは次の章の仕事です。
