namespace Molly.Prediction;

/// <summary>예측 진영. A는 도전자, B는 상대다(배틀 로그의 A 파랑·B 빨강과 같다).</summary>
public enum PredictionSide { Challenger, Opponent }

/// <summary>정산 계산에 쓰는 참여 한 건. <paramref name="Order"/>는 처음 건 순서(작을수록 먼저).</summary>
public sealed record PredictionStake(ulong UserId, PredictionSide Side, long Amount, long Order);

/// <summary>공동 배당 계산. DB·Discord와 분리해 테스트한다.</summary>
public static class PredictionPayout
{
    /// <summary>
    /// 승리 진영 참여자별 배당금. 전체 판돈 × 개인 베팅액 ÷ 승리 진영 총베팅액을 내림하고,
    /// 남은 증표는 소수점 나머지가 큰 순 → 베팅액이 큰 순 → 먼저 건 순으로 1개씩 준다. 총합은 항상 전체 판돈과 같다.
    /// 승리 진영에 건 사람이 없으면 빈 결과다(호출한 쪽이 무효 처리).
    /// </summary>
    public static IReadOnlyDictionary<ulong, long> Calculate(IReadOnlyList<PredictionStake> stakes, PredictionSide winner)
    {
        var pool = stakes.Sum(x => x.Amount);
        var winners = stakes.Where(x => x.Side == winner).ToList();
        var winningTotal = winners.Sum(x => x.Amount);
        if (winningTotal <= 0) return new Dictionary<ulong, long>();

        var shares = winners.Select(x =>
        {
            var exact = (Int128)pool * x.Amount;
            return (Stake: x, Base: (long)(exact / winningTotal), Remainder: (long)(exact % winningTotal));
        }).ToList();
        var result = shares.ToDictionary(x => x.Stake.UserId, x => x.Base);
        var leftover = pool - shares.Sum(x => x.Base);
        foreach (var share in shares.OrderByDescending(x => x.Remainder).ThenByDescending(x => x.Stake.Amount).ThenBy(x => x.Stake.Order).Take((int)leftover))
            result[share.Stake.UserId]++;
        return result;
    }

    /// <summary>
    /// 마감 때 보여줄 진영별 비율(정수 퍼센트, 합 100). 실제 수량은 공개하지 않는다.
    /// 한쪽에만 걸렸으면 100/0, 아무도 걸지 않았으면 null.
    /// </summary>
    public static (int Challenger, int Opponent)? SharePercent(long challengerTotal, long opponentTotal)
    {
        var total = challengerTotal + opponentTotal;
        if (total <= 0) return null;
        var challenger = (int)Math.Round(challengerTotal * 100m / total, MidpointRounding.AwayFromZero);
        // 한쪽이라도 걸렸으면 0%로 보이지 않게 1~99로 둔다.
        if (challengerTotal > 0 && opponentTotal > 0) challenger = Math.Clamp(challenger, 1, 99);
        return (challenger, 100 - challenger);
    }
}
