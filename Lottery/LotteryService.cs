using System.Collections.Concurrent;

namespace Molly.Lottery;

public sealed record LotteryOutcome(IReadOnlyList<LotteryResult> Results, string Reason);

/// <summary>길드마다 하나의 당첨뽑기만 유지하고, 시간 종료와 강제 종료를 안전하게 처리합니다.</summary>
public sealed class LotteryService(Action<string>? log = null)
{
    private readonly ConcurrentDictionary<ulong, Game> _games = new();
    private readonly Action<string> _log = log ?? Console.WriteLine;

    public bool TryStart(ulong guildId, ulong channelId, IEnumerable<ulong> participants, int winnerCount, TimeSpan timeout,
        Func<LotteryOutcome, Task> announceRemaining)
    {
        var game = new Game(channelId, new LotteryRound(participants, winnerCount), timeout, announceRemaining);
        if (!_games.TryAdd(guildId, game)) return false;
        if (timeout > TimeSpan.Zero) _ = RunTimeoutAsync(guildId, game);
        return true;
    }

    public async Task<LotteryDrawResult> DrawAsync(ulong guildId, ulong channelId, ulong userId)
    {
        if (!_games.TryGetValue(guildId, out var game) || game.ChannelId != channelId) return LotteryDrawResult.Ended;
        var result = game.Draw(userId);
        if ((result is LotteryDrawResult.Winner or LotteryDrawResult.NotWinner) && game.Round.AllRevealed)
            FinishSilently(guildId, game);
        await Task.CompletedTask;
        return result;
    }

    public Task<bool> RevealImmediatelyAsync(ulong guildId, string reason) => EndAsync(guildId, reason);

    public async Task<bool> EndAsync(ulong guildId, string reason)
    {
        if (!_games.TryGetValue(guildId, out var game) || !game.TryFinish()) return false;
        _games.TryRemove(new KeyValuePair<ulong, Game>(guildId, game));
        try
        {
            await game.AnnounceRemaining(new LotteryOutcome(game.Round.RevealRemaining(), reason));
        }
        catch (Exception ex) { _log($"[당첨뽑기] 결과 안내 실패: {ex.Message}"); }
        return true;
    }

    public void CancelChannel(ulong channelId)
    {
        foreach (var (guildId, game) in _games)
            if (game.ChannelId == channelId && _games.TryRemove(new KeyValuePair<ulong, Game>(guildId, game))) game.TryFinish();
    }

    private async Task RunTimeoutAsync(ulong guildId, Game game)
    {
        try
        {
            await Task.Delay(game.Timeout, game.Cancellation.Token);
            await EndAsync(guildId, "제한시간이 끝났어요.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[당첨뽑기] 시간 종료 처리 실패: {ex.Message}"); }
    }

    private void FinishSilently(ulong guildId, Game game)
    {
        if (!game.TryFinish()) return;
        _games.TryRemove(new KeyValuePair<ulong, Game>(guildId, game));
    }

    private sealed class Game(ulong channelId, LotteryRound round, TimeSpan timeout, Func<LotteryOutcome, Task> announceRemaining)
    {
        private int _finished;
        private readonly object _gate = new();
        public ulong ChannelId { get; } = channelId;
        public LotteryRound Round { get; } = round;
        public TimeSpan Timeout { get; } = timeout;
        public Func<LotteryOutcome, Task> AnnounceRemaining { get; } = announceRemaining;
        public CancellationTokenSource Cancellation { get; } = new();
        public LotteryDrawResult Draw(ulong userId)
        {
            lock (_gate)
                return _finished == 0 ? Round.Draw(userId) : LotteryDrawResult.Ended;
        }
        public bool TryFinish()
        {
            lock (_gate)
            {
                if (_finished != 0) return false;
                _finished = 1;
            }
            Cancellation.Cancel();
            return true;
        }
    }
}
