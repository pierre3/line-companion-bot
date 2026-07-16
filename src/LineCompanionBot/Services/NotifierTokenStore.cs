using System.Collections.Concurrent;
using Line.OpenApi.MiniApp.Models;

namespace LineCompanionBot.Services;

// Holds the latest NotifierToken per user. Overwritten whenever a token is (re-)issued or renewed
// by a send — no history needed, only the most recent token is ever usable.
public sealed class NotifierTokenStore
{
    private readonly ConcurrentDictionary<string, NotifierToken> _tokens = new();

    public void Save(string userId, NotifierToken token) => _tokens[userId] = token;

    public bool TryGet(string userId, out NotifierToken token) => _tokens.TryGetValue(userId, out token!);

    public void Remove(string userId) => _tokens.TryRemove(userId, out _);
}
