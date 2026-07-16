using System.Collections.Concurrent;

namespace LineCompanionBot.Services;

// In-memory only, no persistence — the app is a demo, not a game server. State resets on restart.
public sealed class PetStore
{
    private const string DefaultName = "Pico";
    private const double InitialHunger = 80.0;
    private const double InitialHappiness = 80.0;

    private readonly ConcurrentDictionary<string, PetState> _pets = new();

    public PetState GetOrCreate(string userId, DateTimeOffset now)
        => _pets.GetOrAdd(userId, id => new PetState(id, DefaultName, InitialHunger, InitialHappiness, Xp: 0, now));

    public void Save(PetState state) => _pets[state.UserId] = state;
}
