using System.Collections.Concurrent;

namespace LineCompanionBot.Services;

public sealed record ShopOrder(string OrderId, string UserId, string ProductId);

// Recorded at reserve time (Chapter 6), consulted at reconciliation time (Chapter 7) to confirm an
// IAP webhook event corresponds to an order this app actually initiated.
public sealed class OrderStore
{
    private readonly ConcurrentDictionary<string, ShopOrder> _orders = new();

    public void Record(string orderId, string userId, string productId)
        => _orders[orderId] = new ShopOrder(orderId, userId, productId);

    public bool TryGet(string orderId, out ShopOrder order) => _orders.TryGetValue(orderId, out order!);
}
