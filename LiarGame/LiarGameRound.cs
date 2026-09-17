namespace Molly.LiarGame;

/// <summary>Discord 입출력과 분리한 라이어게임의 투표 판정 규칙입니다.</summary>
public sealed class LiarGameRound
{
    private readonly HashSet<ulong> _participants;
    private readonly Dictionary<ulong, bool> _answers = new();
    private readonly Dictionary<ulong, ulong> _accusations = new();

    public LiarGameRound(IEnumerable<ulong> participants)
    {
        _participants = participants.Distinct().ToHashSet();
        if (_participants.Count < 3) throw new ArgumentException("참가자는 3명 이상이어야 합니다.");
    }

    public bool VoteAnswer(ulong userId, bool answer)
    {
        if (!_participants.Contains(userId) || _answers.ContainsKey(userId)) return false;
        _answers[userId] = answer;
        return true;
    }

    public IReadOnlyDictionary<ulong, bool> Answers => _answers;
    public IReadOnlyCollection<ulong> MissingAnswers => _participants.Where(id => !_answers.ContainsKey(id)).Order().ToArray();
    public bool AllAnswered => _answers.Count == _participants.Count;

    public bool Accuse(ulong voterId, ulong targetId)
    {
        if (!_participants.Contains(voterId) || !_participants.Contains(targetId) || _accusations.ContainsKey(voterId)) return false;
        _accusations[voterId] = targetId;
        return true;
    }

    public bool AllAccused => _accusations.Count == _participants.Count;
    public IReadOnlyDictionary<ulong, ulong> Accusations => _accusations;

    public AccusationResult Decide(ulong liarId)
    {
        if (_accusations.Count == 0) return new(false, null, "지목한 사람이 없어 라이어의 승리입니다.");
        var ranked = _accusations.GroupBy(x => x.Value).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).ToArray();
        if (ranked.Length > 1 && ranked[0].Count() == ranked[1].Count()) return new(false, null, "최다 득표가 동률이라 라이어의 승리입니다.");
        var selected = ranked[0].Key;
        return selected == liarId
            ? new(true, selected, "라이어를 지목했습니다!")
            : new(false, selected, "라이어가 아닌 사람이 지목되어 라이어의 승리입니다.");
    }
}

public sealed record AccusationResult(bool LiarFound, ulong? SelectedUserId, string Reason);
