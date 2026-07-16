using Microsoft.Extensions.DependencyInjection;

namespace LineCompanionBot.Persistence.InMemory;

// The seam a real deployment swaps out: replace this one call in Program.cs with, e.g.,
// AddSqlPersistence(connectionString) — every consumer depends on the I*Store interfaces, not on
// these types, so no other line changes. Singleton here because the in-memory dictionaries must
// outlive any single request; an RDB-backed implementation would typically register as Scoped
// instead (per-request DbContext), which is why PurchaseReconciliationService resolves stores from
// a fresh DI scope per poll rather than taking them as direct constructor dependencies.
public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IPetStore, InMemoryPetStore>();
        services.AddSingleton<IOrderStore, InMemoryOrderStore>();
        services.AddSingleton<IInventoryStore, InMemoryInventoryStore>();
        services.AddSingleton<INotifierTokenStore, InMemoryNotifierTokenStore>();
        return services;
    }
}
