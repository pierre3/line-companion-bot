using System.Collections.Concurrent;

namespace LineCompanionBot.Persistence.InMemory;

public sealed class InMemoryInventoryStore : IInventoryStore
{
    private readonly ConcurrentDictionary<string, List<InventoryItem>> _inventory = new();

    // Snapshotted under the same lock Grant/Revoke use — Get is reachable from a GET endpoint
    // concurrently with the background reconciliation loop mutating the same List<T>, which is
    // not thread-safe for an unsynchronized read against a locked writer.
    public Task<IReadOnlyList<InventoryItem>> GetAsync(string userId, CancellationToken ct = default)
    {
        if (!_inventory.TryGetValue(userId, out var list))
            return Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        lock (list) { return Task.FromResult<IReadOnlyList<InventoryItem>>(list.ToArray()); }
    }

    public Task<bool> GrantAsync(string userId, string orderId, string productId, CancellationToken ct = default)
    {
        var list = _inventory.GetOrAdd(userId, _ => new List<InventoryItem>());
        lock (list)
        {
            if (list.Any(i => i.OrderId == orderId)) return Task.FromResult(false);
            list.Add(new InventoryItem(orderId, productId));
            return Task.FromResult(true);
        }
    }

    public Task<bool> RevokeAsync(string userId, string orderId, CancellationToken ct = default)
    {
        if (!_inventory.TryGetValue(userId, out var list)) return Task.FromResult(false);
        lock (list) { return Task.FromResult(list.RemoveAll(i => i.OrderId == orderId) > 0); }
    }

    public Task<bool> TryConsumeAsync(string userId, string productId, CancellationToken ct = default)
    {
        if (!_inventory.TryGetValue(userId, out var list)) return Task.FromResult(false);
        lock (list)
        {
            var index = list.FindIndex(i => i.ProductId == productId);
            if (index < 0) return Task.FromResult(false);
            list.RemoveAt(index);
            return Task.FromResult(true);
        }
    }
}
