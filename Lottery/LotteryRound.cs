namespace Molly.Lottery;

/// <summary>당첨뽑기의 참가자·당첨자·공개 상태를 한 번의 추첨 단위로 관리합니다.</summary>
public sealed class LotteryRound
{
    private readonly HashSet<ulong> _participants;
    private readonly HashSet<ulong> _winnerIds;
    private readonly HashSet<ulong> _revealedIds = [];
    private readonly object _gate = new();

    public LotteryRound(IEnumerable<ulong> participants, int winnerCount, Random? random = null)
    {
        var ids = participants.Distinct().ToArray();
        if (ids.Length == 0) throw new ArgumentException("참가자가 필요합니다.", nameof(participants));
        if (winnerCount < 1 || winnerCount > ids.Length) throw new ArgumentOutOfRangeException(nameof(winnerCount));

        _participants = ids.ToHashSet();
        random ??= Random.Shared;
        _winnerIds = ids.OrderBy(_ => random.Next()).Take(winnerCount).ToHashSet();
    }

    public int ParticipantCount => _participants.Count;
    public int WinnerCount => _winnerIds.Count;

    public LotteryDrawResult Draw(ulong userId)
    {
        lock (_gate)
        {
            if (!_participants.Contains(userId)) return LotteryDrawResult.NotParticipant;
            if (!_revealedIds.Add(userId)) return LotteryDrawResult.AlreadyDrawn;
            return _winnerIds.Contains(userId) ? LotteryDrawResult.Winner : LotteryDrawResult.NotWinner;
        }
    }

    public IReadOnlyList<LotteryResult> RevealRemaining()
    {
        lock (_gate)
        {
            var remaining = _participants.Where(id => _revealedIds.Add(id))
                .Select(id => new LotteryResult(id, _winnerIds.Contains(id)))
                .ToArray();
            return remaining;
        }
    }

    public bool AllRevealed
    {
        get { lock (_gate) return _revealedIds.Count == _participants.Count; }
    }
}

public enum LotteryDrawResult { Winner, NotWinner, NotParticipant, AlreadyDrawn, Ended }
public sealed record LotteryResult(ulong UserId, bool IsWinner);
