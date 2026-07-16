using Line.OpenApi.MiniApp.Models;

namespace LineCompanionBot.Persistence;

// Holds the latest NotifierToken per user. Overwritten whenever a token is (re-)issued or renewed
// by a send — no history needed, only the most recent token is ever usable.
public interface INotifierTokenStore
{
    Task SaveAsync(string userId, NotifierToken token, CancellationToken ct = default);

    Task<NotifierToken?> TryGetAsync(string userId, CancellationToken ct = default);
}
