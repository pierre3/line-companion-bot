using System.Collections.Concurrent;

namespace LineCompanionBot.Persistence.InMemory;

public sealed class InMemoryOrderStore : IOrderStore
{
    private readonly ConcurrentDictionary<string, ShopOrder> _orders = new();

    public Task RecordAsync(string orderId, string userId, string productId, CancellationToken ct = default)
    {
        _orders[orderId] = new ShopOrder(orderId, userId, productId);
        return Task.CompletedTask;
    }

    public Task<ShopOrder?> TryGetAsync(string orderId, CancellationToken ct = default)
        => Task.FromResult(_orders.TryGetValue(orderId, out var order) ? order : null);
}
