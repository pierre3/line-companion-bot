namespace LineCompanionBot.Services;

public sealed record ShopItem(string ProductId, string Name, string Description);

// Fixed in-memory catalog — no admin CRUD, this is a demo shop with a handful of items.
public static class ShopCatalog
{
    public static readonly IReadOnlyList<ShopItem> Items = new List<ShopItem>
    {
        new("rare-food", "Golden Kibble", "A rare treat — refills Hunger to full instantly."),
        new("party-hat", "Party Hat", "A cosmetic hat for your companion."),
        new("star-badge", "Star Badge", "A shiny cosmetic badge to show off."),
    };

    public static ShopItem? Find(string productId) => Items.FirstOrDefault(i => i.ProductId == productId);
}
