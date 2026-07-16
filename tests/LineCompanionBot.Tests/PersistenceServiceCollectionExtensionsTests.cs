using LineCompanionBot.Persistence;
using LineCompanionBot.Persistence.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LineCompanionBot.Tests;

public class PersistenceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddInMemoryPersistence_RegistersAllFourStoresAsSingletons()
    {
        var provider = new ServiceCollection()
            .AddInMemoryPersistence()
            .BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IPetStore>(), provider.GetRequiredService<IPetStore>());
        Assert.Same(provider.GetRequiredService<IOrderStore>(), provider.GetRequiredService<IOrderStore>());
        Assert.Same(provider.GetRequiredService<IInventoryStore>(), provider.GetRequiredService<IInventoryStore>());
        Assert.Same(provider.GetRequiredService<INotifierTokenStore>(), provider.GetRequiredService<INotifierTokenStore>());
    }
}
