namespace Molly.RuneEnhance;

/// <summary>촉매 사용 방식. 혼합 전략·최적 전략 추천은 지원하지 않습니다.</summary>
public enum CatalystPlan
{
    /// <summary>촉매를 쓰지 않습니다.</summary>
    None,
    /// <summary>촉매를 쓸 수 있는 단계(CatalystCount &gt; 0)에서는 매번 필요한 만큼 씁니다.</summary>
    WheneverAvailable,
}

/// <summary>예상 소모량(평균). 표시용 반올림 전 값입니다.</summary>
public readonly record struct RuneEnhanceCost(double Gold, double Shards, double Catalysts, double PaidAttempts)
{
    public static RuneEnhanceCost operator -(RuneEnhanceCost a, RuneEnhanceCost b) =>
        new(a.Gold - b.Gold, a.Shards - b.Shards, a.Catalysts - b.Catalysts, a.PaidAttempts - b.PaidAttempts);
}

/// <summary>
/// 현재 단계·포인트에서 목표 단계까지 각인 강화 예상 소모량(평균)을 정확히 계산합니다.
/// 무작위 시뮬레이션이 아니라 정수 강화 포인트 상태 전체에 대한 기댓값 점화식을 목표 단계부터 거꾸로 풉니다.
/// 상태는 (단계 L, 그 단계 안의 포인트 x)이고, 시도 결과는 <see cref="RuneEnhanceRules"/>를 따릅니다.
/// - 촉매 미사용: 성공 p → (L+1, x), 실패 → (L, x + 실패 포인트)
/// - 촉매 사용: 성공·성공 p² → (L+1, x + 폭), 성공·실패·실패·성공 2p(1−p) → (L+1, x + 실패 포인트), 실패·실패 (1−p)² → (L, x + 실패 포인트 × 2)
/// - x가 폭 이상이면 비용 없이 (L+1, x − 폭)으로 넘어갑니다(확정 강화, 연쇄 가능).
/// 실패 포인트가 항상 양수라 x는 시도마다 늘어나므로 같은 단계 안에서는 큰 x부터 계산하면 됩니다.
/// </summary>
public static class RuneEnhanceCalculator
{
    public static RuneEnhanceCost Estimate(RuneEnhanceCostTable table, RuneEnhanceRequest request, CatalystPlan plan) =>
        Estimate(table, request.Current, request.StartPoints, request.Target, plan);

    public static RuneEnhanceCost Estimate(RuneEnhanceCostTable table, int current, long startPoints, int target, CatalystPlan plan)
    {
        if (current < 0 || target > table.MaxLevel || target < current) throw new ArgumentOutOfRangeException(nameof(target));
        if (startPoints < 0) throw new ArgumentOutOfRangeException(nameof(startPoints));
        // 자원마다 같은 점화식을 따로 풉니다. 상태 배열을 자원 하나씩만 들고 있어 메모리가 적습니다(최대 약 12MB).
        return new RuneEnhanceCost(
            Solve(table, current, startPoints, target, plan, (step, catalyst) => step.Gold),
            Solve(table, current, startPoints, target, plan, (step, catalyst) => step.ShardAmount),
            Solve(table, current, startPoints, target, plan, (step, catalyst) => catalyst ? step.CatalystCount : 0),
            Solve(table, current, startPoints, target, plan, (step, catalyst) => 1));
    }

    /// <summary>이 단계의 시도에 촉매를 쓰는지.</summary>
    public static bool UsesCatalyst(RuneEnhanceStep step, CatalystPlan plan) =>
        plan == CatalystPlan.WheneverAvailable && step.CatalystCount > 0 && step.SuccessProbability < 1;

    private static double Solve(RuneEnhanceCostTable table, int current, long startPoints, int target, CatalystPlan plan,
        Func<RuneEnhanceStep, bool, double> attemptCost)
    {
        if (current == target) return 0;
        // values[L][x] = (L, x)에서 목표까지의 기댓값. 필요한 단계(L+1, L+2)만 남기고 버립니다.
        var values = new double[]?[target + 1];

        // (level, x)의 기댓값. x가 폭 이상이면 확정 강화로 다음 단계에 넘깁니다.
        double Value(int level, long x)
        {
            while (level < target && x >= table.Width(level))
            {
                x -= table.Width(level);
                level++;
            }
            return level >= target ? 0 : values[level]![x];
        }

        for (var level = target - 1; level >= current; level--)
        {
            var step = table.StepFrom(level);
            var width = table.Width(level);
            var p = step.SuccessProbability;
            var catalyst = UsesCatalyst(step, plan);
            var cost = attemptCost(step, catalyst);
            var array = new double[width];
            values[level] = array;
            if (p >= 1)
            {
                for (var x = width - 1; x >= 0; x--)
                    array[x] = cost + Value(level + 1, x);
            }
            else
            {
                var fail = table.FailPoints(level);
                if (fail <= 0) throw new InvalidOperationException($"+{level} 실패 포인트가 0이라 계산할 수 없습니다.");
                var q = 1 - p;
                for (var x = width - 1; x >= 0; x--)
                {
                    array[x] = catalyst
                        ? cost + p * p * Value(level + 1, x + width) + 2 * p * q * Value(level + 1, x + fail) + q * q * Value(level, x + 2 * fail)
                        : cost + p * Value(level + 1, x) + q * Value(level, x + fail);
                }
            }
            if (level + 3 <= target) values[level + 3] = null;
        }
        return Value(current, startPoints);
    }
}
