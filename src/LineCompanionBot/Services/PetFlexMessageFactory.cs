using Line.OpenApi.Messaging.Generated.Api.Models;

namespace LineCompanionBot.Services;

// What a feed/play action just restored, surfaced on the status card so the three actions don't all
// render identically. Null on a plain status check (nothing was restored).
public sealed record CareFeedback(int HungerGain, int HappinessGain);

// Hand-built Flex Message bubbles: FlexBubble/FlexBox/FlexText are pure generated POCOs with no
// facade in Line.OpenApi.Messaging, so this is the only place that shape gets assembled. Each POCO's
// Type discriminator ("flex"/"bubble"/"box"/"text") has no default and is written out only when set,
// so it must be assigned explicitly — otherwise it is omitted and LINE rejects the body with a 400.
public static class PetFlexMessageFactory
{
    public static FlexMessage BuildStatus(PetState state, int rareFoodCount, CareFeedback? feedback = null)
    {
        var level = PetGrowthEngine.Level(state);
        var stage = PetGrowthEngine.Stage(state);
        var xpToNext = PetGrowthEngine.XpPerLevel - state.Xp % PetGrowthEngine.XpPerLevel;

        var contents = new List<FlexComponent>();

        // Prominent centered banner for the amount the just-taken action restored, so feed/play/status
        // read differently. Given its own line at the top rather than appended to a bar, which overflowed
        // the card width on narrow screens.
        var banner = GainBanner(feedback);
        if (banner is not null)
            contents.Add(banner);

        contents.Add(new FlexText { Type = "text", Text = $"{StageEmoji(stage)} Lv.{level} ({stage})", Weight = FlexText_weight.Bold, Size = "lg", Margin = banner is null ? null : "md" });
        contents.Add(new FlexText { Type = "text", Text = $"XP {state.Xp} · {xpToNext} to next Lv", Size = "sm", Margin = "md" }); // ·
        contents.Add(new FlexText { Type = "text", Text = $"Hunger {Bar(state.Hunger)} {(int)state.Hunger}%", Size = "sm", Margin = "md" });
        contents.Add(new FlexText { Type = "text", Text = $"Happy  {Bar(state.Happiness)} {(int)state.Happiness}%", Size = "sm" });

        // Only surfaced once the user actually owns rare food — no line (and no "x0" clutter) otherwise.
        if (rareFoodCount > 0)
            contents.Add(new FlexText { Type = "text", Text = $"\U0001F356 Golden Kibble ×{rareFoodCount}", Size = "sm", Margin = "md" }); // 🍖 ×

        var body = new FlexBox
        {
            Type = "box",
            Layout = FlexBox_layout.Vertical,
            Contents = contents,
        };

        var header = new FlexBox
        {
            Type = "box",
            Layout = FlexBox_layout.Vertical,
            Contents = new List<FlexComponent> { new FlexText { Type = "text", Text = state.Name, Weight = FlexText_weight.Bold, Size = "xl" } },
        };

        var altText = $"{state.Name}: Lv.{level}, Hunger {(int)state.Hunger}%, Happy {(int)state.Happiness}%";
        if (rareFoodCount > 0)
            altText += $", Golden Kibble x{rareFoodCount}";

        return new FlexMessage
        {
            Type = "flex",
            AltText = altText,
            Contents = new FlexBubble { Type = "bubble", Header = header, Body = body },
        };
    }

    public static FlexMessage BuildPlayRefused(PetState state)
    {
        var body = new FlexBox
        {
            Type = "box",
            Layout = FlexBox_layout.Vertical,
            Contents = new List<FlexComponent>
            {
                new FlexText { Type = "text", Text = $"{state.Name} is too hungry to play.", Weight = FlexText_weight.Bold, Wrap = true },
                new FlexText { Type = "text", Text = "Feed first, then try again.", Size = "sm", Margin = "md", Wrap = true },
            },
        };

        return new FlexMessage
        {
            Type = "flex",
            AltText = $"{state.Name} is too hungry to play.",
            Contents = new FlexBubble { Type = "bubble", Body = body },
        };
    }

    // A big, centered "+N" line for the stat the action just restored, or null on a plain status check.
    private static FlexText? GainBanner(CareFeedback? feedback)
    {
        if (feedback is { HungerGain: > 0 } fed)
            return new FlexText { Type = "text", Text = $"\U0001F354 +{fed.HungerGain}", Weight = FlexText_weight.Bold, Size = "xxl", Align = FlexText_align.Center, Color = "#F0932B" }; // 🍔
        if (feedback is { HappinessGain: > 0 } played)
            return new FlexText { Type = "text", Text = $"❤ +{played.HappinessGain}", Weight = FlexText_weight.Bold, Size = "xxl", Align = FlexText_align.Center, Color = "#EB4D4B" }; // ❤
        return null;
    }

    private static string StageEmoji(PetStage stage) => stage switch
    {
        PetStage.Hatchling => "\U0001F95A", // egg
        PetStage.Juvenile => "\U0001F423",  // hatching chick
        PetStage.Adult => "\U0001F414",     // chicken
        _ => "?",
    };

    private static string Bar(double percent)
    {
        var filled = Math.Clamp((int)Math.Round(percent / 10), 0, 10);
        return new string('█', filled) + new string('░', 10 - filled);
    }
}
