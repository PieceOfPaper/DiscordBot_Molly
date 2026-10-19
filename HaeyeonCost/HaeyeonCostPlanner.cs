namespace Molly.HaeyeonCost;

public enum HaeyeonCostInputError
{
    None,
    // 현재 또는 목표가 지원 범위(7,200~10,000) 밖
    OutOfRange,
    // 현재 저항이 초기 7,200에서 14 단위로 오를 수 있는 값이 아님(다른 룬·장비 구성이 섞였을 수 있음)
    OffGrid,
}

/// <summary>
/// 계산 결과. 수량은 모두 초기 세팅 이후의 추가 구매·감정 수입니다.
/// EstimatedCurrent는 평균 시세로 현재 저항에 가장 싸게 도달하는 대표 상태이며, 실제 사용자 장비가 아닙니다.
/// </summary>
public sealed record HaeyeonCostPlan(
    int CurrentResistance,
    int TargetResistance,
    int ReachedResistance,
    IReadOnlyDictionary<HaeyeonSlot, int> EstimatedCurrent,
    IReadOnlyDictionary<HaeyeonSlot, int> Purchases,
    decimal ExactCost)
{
    // 평균 단가는 반올림하지 않고 합산한 뒤 최종 금액만 올림합니다.
    public long Cost => (long)decimal.Ceiling(ExactCost);
}

/// <summary>
/// 해연 장비 추가 구매 비용을 슬롯별 그룹 배낭 DP로 계산합니다. 저항은 14 단위(최대 200단위)라 상태 수가 작습니다.
/// 1) 초기 상태에서 현재 저항과 정확히 같은 최소 비용 상태를 대표 현재 상태로 추정합니다.
/// 2) 그 상태의 진행도를 유지한 채(되돌리기·재배치·판매 없음) 목표 이상에 도달하는 최소 추가 비용 조합을 찾습니다.
/// 동률은 슬롯 순서(HaeyeonSlot)와 적은 수량을 먼저 고르고, 최종 비용이 같으면 도달 저항이 높은 조합을 고릅니다.
/// </summary>
public static class HaeyeonCostPlanner
{
    public const int MaxUnits = (HaeyeonCostRules.MaxResistance - HaeyeonCostRules.InitialResistance) / HaeyeonCostRules.Step;

    public static HaeyeonCostInputError Validate(int current, int target)
    {
        if (current is < HaeyeonCostRules.MinInput or > HaeyeonCostRules.MaxInput || target is < HaeyeonCostRules.MinInput or > HaeyeonCostRules.MaxInput)
            return HaeyeonCostInputError.OutOfRange;
        return (current - HaeyeonCostRules.InitialResistance) % HaeyeonCostRules.Step == 0 ? HaeyeonCostInputError.None : HaeyeonCostInputError.OffGrid;
    }

    public static HaeyeonCostPlan Plan(int current, int target, IReadOnlyDictionary<HaeyeonSlot, decimal> prices)
    {
        if (Validate(current, target) != HaeyeonCostInputError.None) throw new ArgumentException("지원하지 않는 입력입니다.");
        var initial = Enum.GetValues<HaeyeonSlot>().ToDictionary(x => x, _ => 0);
        var estimate = Solve(initial, prices)[(current - HaeyeonCostRules.InitialResistance) / HaeyeonCostRules.Step]
            ?? throw new InvalidOperationException("현재 저항에 도달하는 상태가 없습니다.");
        var needed = Math.Max(0, (target - current + HaeyeonCostRules.Step - 1) / HaeyeonCostRules.Step);

        var extra = Solve(estimate.Counts, prices);
        var best = -1;
        for (var units = needed; units < extra.Length; units++)
            if (extra[units] is { } option && (best < 0 || option.Cost <= extra[best]!.Value.Cost))
                best = units;
        var chosen = extra[best]!.Value;
        var purchases = chosen.Counts.Where(x => x.Value > estimate.Counts[x.Key])
            .ToDictionary(x => x.Key, x => x.Value - estimate.Counts[x.Key]);
        return new HaeyeonCostPlan(current, target, current + best * HaeyeonCostRules.Step, estimate.Counts, purchases, chosen.Cost);
    }

    /// <summary>baseCounts에서 출발해 정확히 u단위 더 오르는 최소 비용과 그때의 슬롯별 수량(절대값). 도달할 수 없으면 null.</summary>
    private static (decimal Cost, IReadOnlyDictionary<HaeyeonSlot, int> Counts)?[] Solve(
        IReadOnlyDictionary<HaeyeonSlot, int> baseCounts, IReadOnlyDictionary<HaeyeonSlot, decimal> prices)
    {
        var slots = Enum.GetValues<HaeyeonSlot>();
        var cost = new decimal?[MaxUnits + 1];
        cost[0] = 0;
        var choices = new int[slots.Length][];
        for (var g = 0; g < slots.Length; g++)
        {
            var slot = slots[g];
            var start = baseCounts[slot];
            var startUnits = HaeyeonCostRules.Gain(slot, start) / HaeyeonCostRules.Step;
            var next = new decimal?[MaxUnits + 1];
            choices[g] = new int[MaxUnits + 1];
            for (var before = 0; before <= MaxUnits; before++)
            {
                if (cost[before] is not { } spent) continue;
                for (var count = start; count <= HaeyeonCostRules.MaxCount(slot); count++)
                {
                    var units = before + HaeyeonCostRules.Gain(slot, count) / HaeyeonCostRules.Step - startUnits;
                    if (units > MaxUnits) break;
                    var total = spent + (count - start) * prices[slot];
                    if (next[units] is { } existing && existing <= total) continue;
                    next[units] = total;
                    choices[g][units] = count;
                }
            }
            cost = next;
        }

        var result = new (decimal, IReadOnlyDictionary<HaeyeonSlot, int>)?[MaxUnits + 1];
        for (var units = 0; units <= MaxUnits; units++)
        {
            if (cost[units] is not { } total) continue;
            var counts = new Dictionary<HaeyeonSlot, int>();
            var remaining = units;
            for (var g = slots.Length - 1; g >= 0; g--)
            {
                var slot = slots[g];
                var count = choices[g][remaining];
                counts[slot] = count;
                remaining -= (HaeyeonCostRules.Gain(slot, count) - HaeyeonCostRules.Gain(slot, baseCounts[slot])) / HaeyeonCostRules.Step;
            }
            result[units] = (total, counts);
        }
        return result;
    }
}
