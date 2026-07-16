namespace LineCompanionBot.Persistence;

public sealed record InventoryItem(string OrderId, string ProductId);

public interface IInventoryStore
{
    Task<IReadOnlyList<InventoryItem>> GetAsync(string userId, CancellationToken ct = default);

    // Keyed by OrderId so re-scanning an overlapping poll window (e.g. after a restart) can never
    // double-grant the same purchase — this is what makes reconciliation safe to run idempotently.
    Task<bool> GrantAsync(string userId, string orderId, string productId, CancellationToken ct = default);

    Task<bool> RevokeAsync(string userId, string orderId, CancellationToken ct = default);

    // Consumes one matching item (e.g. a single-use rare food), removing it. Safe to remove rather
    // than flag-as-used: the watermark never re-scans history after a restart, so there is no path
    // that could re-grant (and thus need to re-find) an already-consumed item.
    Task<bool> TryConsumeAsync(string userId, string productId, CancellationToken ct = default);
}
