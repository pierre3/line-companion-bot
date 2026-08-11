using Line.OpenApi.Messaging.Generated.Api.Models;
using LineCompanionBot.Services;
using Xunit;

namespace LineCompanionBot.Tests;

// Regression guard for the "type" discriminator on the hand-built Flex POCOs. Line.OpenApi.Messaging's
// FlexMessage/FlexBubble/FlexBox/FlexText leave the "type" property unset by default and serialize it
// only when assigned, so omitting it produces a message body LINE rejects with 400 — a bug the
// offline-first walkthrough never caught because it built the objects but never sent them. These
// assert every node carries its discriminator.
public class FlexMessageDiscriminatorTests
{
    private static PetState NewState(double hunger = 80, double happiness = 80, int xp = 0)
        => new("user-1", "Pico", hunger, happiness, xp, DateTimeOffset.UtcNow);

    [Fact]
    public void BuildStatus_SetsTypeDiscriminatorOnEveryNode()
    {
        var message = PetFlexMessageFactory.BuildStatus(NewState(), rareFoodCount: 0);

        Assert.Equal("flex", message.Type);
        var bubble = Assert.IsType<FlexBubble>(message.Contents);
        Assert.Equal("bubble", bubble.Type);
        Assert.Equal("box", bubble.Header!.Type);
        Assert.Equal("box", bubble.Body!.Type);
        Assert.All(bubble.Header!.Contents!, c => Assert.Equal("text", c.Type));
        Assert.All(bubble.Body!.Contents!, c => Assert.Equal("text", c.Type));
    }

    [Fact]
    public void BuildPlayRefused_SetsTypeDiscriminatorOnEveryNode()
    {
        var message = PetFlexMessageFactory.BuildPlayRefused(NewState(hunger: 5));

        Assert.Equal("flex", message.Type);
        var bubble = Assert.IsType<FlexBubble>(message.Contents);
        Assert.Equal("bubble", bubble.Type);
        Assert.Equal("box", bubble.Body!.Type);
        Assert.All(bubble.Body!.Contents!, c => Assert.Equal("text", c.Type));
    }
}
