using System.Collections.Concurrent;
using LineCompanionBot.Services;

namespace LineCompanionBot.Persistence.InMemory;

// In-memory only, no persistence — the app is a demo, not a game server. State resets on restart.
// Swap the DI registration for a real IPetStore implementation to persist across restarts.
public sealed class InMemoryPetStore : IPetStore
{
    private const string DefaultName = "Pico";
    private const double InitialHunger = 80.0;
    private const double InitialHappiness = 80.0;

    private readonly ConcurrentDictionary<string, PetState> _pets = new();

    public Task<PetState> GetOrCreateAsync(string userId, DateTimeOffset now, CancellationToken ct = default)
        => Task.FromResult(_pets.GetOrAdd(userId, id => new PetState(id, DefaultName, InitialHunger, InitialHappiness, Xp: 0, now)));

    public Task SaveAsync(PetState state, CancellationToken ct = default)
    {
        _pets[state.UserId] = state;
        return Task.CompletedTask;
    }
}
