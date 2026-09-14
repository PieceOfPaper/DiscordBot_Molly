using System.Collections.Concurrent;

namespace Molly.Nunchi;

public sealed class NunchiGameService(Action<string>? log = null)
{
    private readonly ConcurrentDictionary<ulong, Game> _games = new();
    private readonly Action<string> _log = log ?? Console.WriteLine;

    public bool TryStart(ulong guildId, ulong channelId, IEnumerable<ulong> participants, TimeSpan timeout,
        Func<NunchiOutcome, Task> announceEnd)
    {
        var game = new Game(channelId, participants, timeout, announceEnd);
        if (!_games.TryAdd(guildId, game)) return false;
        _ = RunTimeoutAsync(guildId, game);
        return true;
    }

    public async Task SubmitAsync(ulong guildId, ulong channelId, ulong userId, string content, DateTimeOffset sentAt)
    {
        if (!_games.TryGetValue(guildId, out var game) || game.ChannelId != channelId) return;
        var outcome = game.Round.Submit(userId, content, sentAt);
        if (outcome is not null) await EndAsync(guildId, game, outcome);
    }

    public void CancelChannel(ulong channelId)
    {
        foreach (var (guildId, game) in _games)
        {
            if (game.ChannelId != channelId) continue;
            if (_games.TryRemove(new KeyValuePair<ulong, Game>(guildId, game))) game.Cancel();
        }
    }

    private async Task RunTimeoutAsync(ulong guildId, Game game)
    {
        try
        {
            await Task.Delay(game.Timeout, game.Cancellation.Token);
            await EndAsync(guildId, game, game.Round.Timeout());
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex) { _log($"[눈치게임] 시간 종료 처리 실패: {ex.Message}"); }
    }

    private async Task EndAsync(ulong guildId, Game game, NunchiOutcome outcome)
    {
        if (!game.TryFinish()) return;
        _games.TryRemove(new KeyValuePair<ulong, Game>(guildId, game));
        try { await game.AnnounceEnd(outcome); }
        catch (Exception ex) { _log($"[눈치게임] 종료 안내 실패: {ex.Message}"); }
    }

    private sealed class Game(ulong channelId, IEnumerable<ulong> participants, TimeSpan timeout, Func<NunchiOutcome, Task> announceEnd)
    {
        private int _finished;
        public ulong ChannelId { get; } = channelId;
        public NunchiRound Round { get; } = new(participants);
        public TimeSpan Timeout { get; } = timeout;
        public Func<NunchiOutcome, Task> AnnounceEnd { get; } = announceEnd;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool TryFinish()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return false;
            Cancellation.Cancel();
            return true;
        }

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0) Cancellation.Cancel();
        }
    }
}
