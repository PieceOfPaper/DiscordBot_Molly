using System.Globalization;
using System.Text.RegularExpressions;

namespace Molly.Nunchi;

public sealed record NunchiOutcome(IReadOnlyList<ulong> CaughtUserIds, string Reason);

/// <summary>눈치게임의 숫자·참가자 규칙. 한 라운드는 끝날 때까지 한 번만 결과를 낸다.</summary>
public sealed partial class NunchiRound(IEnumerable<ulong> participants)
{
    private readonly HashSet<ulong> _participants = participants.ToHashSet();
    private readonly HashSet<ulong> _calledUsers = [];
    private readonly List<(ulong UserId, int Number, DateTimeOffset At)> _calls = [];
    private readonly object _gate = new();
    private bool _ended;

    public NunchiOutcome? Submit(ulong userId, string content, DateTimeOffset now)
    {
        if (!NumberPattern().IsMatch(content.Trim()) || !int.TryParse(content.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return null;

        lock (_gate)
        {
            if (_ended || !_participants.Contains(userId)) return null;
            var expected = _calls.Count + 1;
            var last = _calls.LastOrDefault();
            if (_calls.Count > 0 && number == last.Number && now - last.At <= TimeSpan.FromSeconds(1))
                return End([last.UserId, userId], $"마지막 숫자 **{number}**를 1초 안에 중복해서 외쳤어요.");
            if (number != expected)
                return End([userId], $"**{number}**은(는) 지금 외칠 숫자가 아니에요. 다음 숫자는 **{expected}**였어요.");
            if (!_calledUsers.Add(userId))
                return End([userId], "한 사람은 숫자를 한 번만 외칠 수 있어요.");

            _calls.Add((userId, number, now));
            if (_calls.Count == _participants.Count - 1)
                return End(_participants.Except(_calledUsers).ToArray(), "마지막 숫자를 외쳐야 할 사람이 정해졌어요.");
            return null;
        }
    }

    public NunchiOutcome Timeout()
    {
        lock (_gate)
        {
            if (_ended) throw new InvalidOperationException("이미 종료된 눈치게임입니다.");
            return End(_participants.Except(_calledUsers).ToArray(), "제한시간 안에 숫자를 외치지 못했어요.");
        }
    }

    private NunchiOutcome End(IEnumerable<ulong> caught, string reason)
    {
        _ended = true;
        return new NunchiOutcome(caught.Distinct().ToArray(), reason);
    }

    [GeneratedRegex("^\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
