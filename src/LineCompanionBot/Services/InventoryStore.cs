using System.Collections.Concurrent;

namespace LineCompanionBot.Services;

public sealed record InventoryItem(string OrderId, string ProductId);

public sealed class InventoryStore
{
    private readonly ConcurrentDictionary<string, List<InventoryItem>> _inventory = new();

    // Snapshotted under the same lock Grant/Revoke use — Get is reachable from a GET endpoint
    // concurrently with the background reconciliation loop mutating the same List<T>, which is
    // not thread-safe for an unsynchronized read against a locked writer.
    public IReadOnlyList<InventoryItem> Get(string userId)
    {
        if (!_inventory.TryGetValue(userId, out var list)) return Array.Empty<InventoryItem>();
        lock (list) { return list.ToArray(); }
    }

    // Keyed by OrderId so re-scanning an overlapping poll window (e.g. after a restart) can never
    // double-grant the same purchase — this is what makes reconciliation safe to run idempotently.
    public bool Grant(string userId, string orderId, string productId)
    {
        var list = _inventory.GetOrAdd(userId, _ => new List<InventoryItem>());
        lock (list)
        {
            if (list.Any(i => i.OrderId == orderId)) return false;
            list.Add(new InventoryItem(orderId, productId));
            return true;
        }
    }

    public bool Revoke(string userId, string orderId)
    {
        if (!_inventory.TryGetValue(userId, out var list)) return false;
        lock (list) { return list.RemoveAll(i => i.OrderId == orderId) > 0; }
    }

    // Consumes one matching item (e.g. a single-use rare food), removing it. Safe to remove rather
    // than flag-as-used: the watermark never re-scans history after a restart, so there is no path
    // that could re-grant (and thus need to re-find) an already-consumed item.
    public bool TryConsume(string userId, string productId)
    {
        if (!_inventory.TryGetValue(userId, out var list)) return false;
        lock (list)
        {
            var index = list.FindIndex(i => i.ProductId == productId);
            if (index < 0) return false;
            list.RemoveAt(index);
            return true;
        }
    }
}
