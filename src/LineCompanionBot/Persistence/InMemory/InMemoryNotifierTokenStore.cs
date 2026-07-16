using System.Collections.Concurrent;
using Line.OpenApi.MiniApp.Models;

namespace LineCompanionBot.Persistence.InMemory;

public sealed class InMemoryNotifierTokenStore : INotifierTokenStore
{
    private readonly ConcurrentDictionary<string, NotifierToken> _tokens = new();

    public Task SaveAsync(string userId, NotifierToken token, CancellationToken ct = default)
    {
        _tokens[userId] = token;
        return Task.CompletedTask;
    }

    public Task<NotifierToken?> TryGetAsync(string userId, CancellationToken ct = default)
        => Task.FromResult(_tokens.TryGetValue(userId, out var token) ? token : null);
}
