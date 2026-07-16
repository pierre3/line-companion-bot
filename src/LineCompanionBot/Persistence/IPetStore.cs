using LineCompanionBot.Services;

namespace LineCompanionBot.Persistence;

// Async-shaped even though the only implementation today (InMemoryPetStore) never actually awaits
// anything — this is the seam a future RDB-backed store plugs into without changing any caller.
public interface IPetStore
{
    Task<PetState> GetOrCreateAsync(string userId, DateTimeOffset now, CancellationToken ct = default);

    Task SaveAsync(PetState state, CancellationToken ct = default);
}
