using System.Text.Json;
using Molly.Currency;

namespace Molly.Prediction;

/// <summary>
/// 한 예측에 고정하는 규칙. 예측을 열 때 `증표규칙` 시트의 현재 값으로 만들어 DB에 JSON으로 남기므로
/// 진행 중에 시트가 바뀌어도 그 예측에는 영향이 없다.
/// </summary>
/// <param name="BetAmounts">버튼 금액. 같은 버튼을 다시 누르면 그만큼 더 건다.</param>
/// <param name="MaxBalanceRatio">가방 잔액 대비 한 예측 누적 상한 비율.</param>
/// <param name="MaxBetAmount">한 예측 누적 고정 상한.</param>
/// <param name="MinPredictorCount">예측이 성립하는 최소 참여 인원.</param>
public sealed record PredictionRules(IReadOnlyList<long> BetAmounts, decimal MaxBalanceRatio, long MaxBetAmount, int MinPredictorCount)
{
    public const string BetAmountsRuleId = "prediction_bet_amounts";
    public const string MaxBalanceRatioRuleId = "prediction_max_balance_ratio";
    public const string MaxBetAmountRuleId = "prediction_max_bet_amount";
    public const string MinPredictorCountRuleId = "prediction_min_predictor_count";

    /// <summary>한 줄에 진영 이름 버튼 1개와 금액 버튼을 두므로 금액은 최대 4개(Discord 한 줄 최대 5개).</summary>
    public const int MaxBetButtons = 4;

    public static PredictionRules Default { get; } = new([10, 50, 100], 0.5m, 500, 2);

    /// <summary>시트 값에서 규칙을 만든다. 행이 없거나 값이 쓸 수 없으면 항목마다 코드 기본값을 쓴다.</summary>
    public static PredictionRules From(TokenRuleTable table)
    {
        var amounts = table.GetIntegerList(BetAmountsRuleId, Default.BetAmounts);
        if (amounts.Count is 0 or > MaxBetButtons || amounts.Any(x => x <= 0) || amounts.Distinct().Count() != amounts.Count)
            amounts = Default.BetAmounts;
        var ratio = table.GetDecimal(MaxBalanceRatioRuleId, Default.MaxBalanceRatio);
        if (ratio is <= 0 or > 1) ratio = Default.MaxBalanceRatio;
        var max = table.GetInteger(MaxBetAmountRuleId, Default.MaxBetAmount);
        if (max <= 0) max = Default.MaxBetAmount;
        // 양쪽 진영에 모두 걸려야 성립하므로 2명 미만은 의미가 없다.
        var min = table.GetInteger(MinPredictorCountRuleId, Default.MinPredictorCount);
        return new(amounts.ToArray(), ratio, max, (int)Math.Clamp(min, 2, 1000));
    }

    /// <summary>한 예측에 걸 수 있는 누적 상한. 쓸 수 있는 잔액이 0 이하면 0.</summary>
    public long LimitFor(long availableBalance)
        => availableBalance <= 0 ? 0 : Math.Min(MaxBetAmount, (long)decimal.Floor(availableBalance * MaxBalanceRatio));

    public string ToJson() => JsonSerializer.Serialize(this);

    public static PredictionRules FromJson(string json)
        => JsonSerializer.Deserialize<PredictionRules>(json) ?? throw new InvalidDataException("예측 규칙 스냅샷을 읽지 못했습니다.");
}
