namespace LineCompanionBot.Persistence;

public sealed record ShopOrder(string OrderId, string UserId, string ProductId);

// Recorded at reserve time (Chapter 6), consulted at reconciliation time (Chapter 7) to confirm an
// IAP webhook event corresponds to an order this app actually initiated.
public interface IOrderStore
{
    Task RecordAsync(string orderId, string userId, string productId, CancellationToken ct = default);

    Task<ShopOrder?> TryGetAsync(string orderId, CancellationToken ct = default);
}
