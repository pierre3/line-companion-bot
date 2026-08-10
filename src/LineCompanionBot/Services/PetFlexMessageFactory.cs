using Line.OpenApi.Messaging.Generated.Api.Models;

namespace LineCompanionBot.Services;

// Hand-built Flex Message bubbles: FlexBubble/FlexBox/FlexText are pure generated POCOs with no
// facade in Line.OpenApi.Messaging, so this is the only place that shape gets assembled. Each POCO's
// Type discriminator ("flex"/"bubble"/"box"/"text") has no default and is written out only when set,
// so it must be assigned explicitly — otherwise it is omitted and LINE rejects the body with a 400.
public static class PetFlexMessageFactory
{
    public static FlexMessage BuildStatus(PetState state)
    {
        var level = PetGrowthEngine.Level(state);
        var stage = PetGrowthEngine.Stage(state);

        var body = new FlexBox
        {
            Type = "box",
            Layout = FlexBox_layout.Vertical,
            Contents = new List<FlexComponent>
            {
                new FlexText { Type = "text", Text = $"{StageEmoji(stage)} Lv.{level} ({stage})", Weight = FlexText_weight.Bold, Size = "lg" },
                new FlexText { Type = "text", Text = $"Hunger {Bar(state.Hunger)} {(int)state.Hunger}%", Size = "sm", Margin = "md" },
                new FlexText { Type = "text", Text = $"Happy  {Bar(state.Happiness)} {(int)state.Happiness}%", Size = "sm" },
            },
        };

        var header = new FlexBox
        {
            Type = "box",
            Layout = FlexBox_layout.Vertical,
            Contents = new List<FlexComponent> { new FlexText { Type = "text", Text = state.Name, Weight = FlexText_weight.Bold, Size = "xl" } },
        };

        return new FlexMessage
        {
            Type = "flex",
            AltText = $"{state.Name}: Lv.{level}, Hunger {(int)state.Hunger}%, Happy {(int)state.Happiness}%",
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
