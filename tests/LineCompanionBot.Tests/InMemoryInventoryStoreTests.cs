using LineCompanionBot.Persistence.InMemory;
using Xunit;

namespace LineCompanionBot.Tests;

public class InMemoryInventoryStoreTests
{
    [Fact]
    public async Task GrantAsync_SameOrderIdTwice_SecondCallIsANoOp()
    {
        // The idempotency guarantee PurchaseReconciliationService relies on to safely re-scan an
        // overlapping poll window (e.g. after a restart) without double-granting.
        var store = new InMemoryInventoryStore();

        var first = await store.GrantAsync("user-1", "order-1", "rare-food");
        var second = await store.GrantAsync("user-1", "order-1", "rare-food");

        Assert.True(first);
        Assert.False(second);
        Assert.Single(await store.GetAsync("user-1"));
    }

    [Fact]
    public async Task RevokeAsync_RemovesTheGrantedItem()
    {
        var store = new InMemoryInventoryStore();
        await store.GrantAsync("user-1", "order-1", "rare-food");

        var revoked = await store.RevokeAsync("user-1", "order-1");

        Assert.True(revoked);
        Assert.Empty(await store.GetAsync("user-1"));
    }

    [Fact]
    public async Task RevokeAsync_UnknownOrderId_ReturnsFalse()
    {
        var store = new InMemoryInventoryStore();

        Assert.False(await store.RevokeAsync("user-1", "no-such-order"));
    }

    [Fact]
    public async Task TryConsumeAsync_RemovesOneMatchingItem()
    {
        var store = new InMemoryInventoryStore();
        await store.GrantAsync("user-1", "order-1", "rare-food");

        var consumed = await store.TryConsumeAsync("user-1", "rare-food");

        Assert.True(consumed);
        Assert.Empty(await store.GetAsync("user-1"));
    }

    [Fact]
    public async Task TryConsumeAsync_NoMatchingItem_ReturnsFalse()
    {
        var store = new InMemoryInventoryStore();
        await store.GrantAsync("user-1", "order-1", "party-hat");

        Assert.False(await store.TryConsumeAsync("user-1", "rare-food"));
    }
}
