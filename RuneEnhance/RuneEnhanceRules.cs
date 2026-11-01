using Molly.Runes;

namespace Molly.RuneEnhance;

/// <summary>
/// 각인 강화 포인트 규칙. 근거는 NEXON NOW 확률 안내(강화 시스템 > 각인 강화)와 실측이며 docs/rune-enhance-cost.md에 정리했습니다.
/// - 성공: 그 단계의 0% → 100% 전체 포인트(<see cref="RuneEnhanceCostTable.Width"/>)를 받고, 100% 초과분은 다음 단계로 넘어갑니다.
/// - 실패: 실패 포인트 round(폭 × 성공률 ÷ 2)를 받습니다(공식 표 532칸과 일치).
/// - 100% 이상: 재료·골드 없이 100% 확률로 강화하고 초과분만 다음 단계로 넘어갑니다. 촉매는 쓸 수 없습니다.
/// - 촉매: 한 번 분량의 재료로 두 번 독립 판정하고, 두 판정의 성공·실패 포인트를 모두 받습니다. 한 번이라도 성공하면 단계가 오릅니다.
/// </summary>
public static class RuneEnhanceRules
{
    /// <summary>지원하는 시즌·별 단계. 이 범위의 전설·신화(전설+·전설++ 포함) 룬이 같은 비용표를 씁니다.</summary>
    public const int SupportedSeason = 2;
    public const int SupportedTier = 10;

    /// <summary>강화 포인트 입력·표시 정밀도(게임 표시와 같은 소수 첫째 자리).</summary>
    public const int PercentDecimals = 1;

    public static long FailPoints(long width, double successProbability) =>
        successProbability >= 1 ? 0 : (long)Math.Round(width * successProbability / 2, MidpointRounding.AwayFromZero);

    /// <summary>게임 표시처럼 둘째 자리 이하를 버린 진행률(0.1% 단위 정수, 예: 395 = 39.5%).</summary>
    public static long DisplayTenths(long points, long width) => points * 1000 / width;

    /// <summary>
    /// 표시 진행률(0.1% 단위)을 내부 포인트로 바꿉니다. 게임은 둘째 자리 이하를 버려 표시하므로 그 표시가 나오는 가장 작은 포인트를 씁니다.
    /// 100%(1000)면 폭과 같아 확정 강화 상태입니다.
    /// </summary>
    public static long PointsFromTenths(long tenths, long width) => (width * tenths + 999) / 1000;

    /// <summary>부위 이름(/룬과 같은 <see cref="RuneNames.Display(RuneCategory)"/>)으로 부위를 찾습니다.</summary>
    public static RuneCategory? ParsePart(string? text) =>
        Enum.GetValues<RuneCategory>().Cast<RuneCategory?>().FirstOrDefault(x => x!.Value.Display() == text?.Trim());
}

/// <summary>입력 검증 결과. 계산할 수 있으면 <see cref="Request"/>, 아니면 사용자에게 보여줄 <see cref="Message"/>가 있습니다.</summary>
public sealed record RuneEnhanceInput(RuneEnhanceRequest? Request, string? Message, bool IsError)
{
    public static RuneEnhanceInput Error(string message) => new(null, message, true);
    public static RuneEnhanceInput Notice(string message) => new(null, message, false);
}

/// <param name="StartTenths">시작 강화 포인트 진행률(0.1% 단위). null이면 생략(0%).</param>
/// <param name="StartPoints">진행률을 바꾼 현재 단계 안의 포인트.</param>
public sealed record RuneEnhanceRequest(RuneCategory Category, int Current, int Target, long? StartTenths, long StartPoints)
{
    public long EffectiveTenths => StartTenths ?? 0;

    /// <summary>
    /// 명령 입력을 검증합니다. 값을 조용히 고치지 않고, 범위 밖·소수 둘째 자리 이상·목표가 현재보다 낮은 입력은 안내 문구를 돌려줍니다.
    /// </summary>
    public static RuneEnhanceInput Validate(RuneEnhanceCostTable table, int current, int target, double? percent)
    {
        var max = table.MaxLevel;
        if (current < 0 || current > max) return RuneEnhanceInput.Error($"현재강화는 0~{max} 사이로 입력해주세요.");
        if (target < 0 || target > max) return RuneEnhanceInput.Error($"목표강화는 0~{max} 사이로 입력해주세요.");
        if (target < current) return RuneEnhanceInput.Error($"목표강화(+{target})가 현재강화(+{current})보다 낮아요. 목표를 현재 이상으로 입력해주세요.");

        long? tenths = null;
        if (percent is { } value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 100)
                return RuneEnhanceInput.Error("강화포인트는 0~100(%) 사이로 입력해주세요.");
            var scaled = Math.Round(value * 10);
            if (Math.Abs(value * 10 - scaled) > 1e-6)
                return RuneEnhanceInput.Error("강화포인트는 게임 표시처럼 소수 첫째 자리까지 입력해주세요(예: 39.5).");
            tenths = (long)scaled;
            if (current == max && tenths > 0)
                return RuneEnhanceInput.Error($"+{max}은 최대 강화 단계라 강화 포인트가 없어요.");
        }

        if (current == target)
            return RuneEnhanceInput.Notice(current == max
                ? $"이미 최대 강화(+{max})예요. 추가로 드는 각인 강화 비용은 없어요."
                : $"현재강화와 목표강화가 같아요(+{current}). 추가로 드는 각인 강화 비용은 없어요.");

        var points = RuneEnhanceRules.PointsFromTenths(tenths ?? 0, table.Width(current));
        return new RuneEnhanceInput(new RuneEnhanceRequest(table.Category, current, target, tenths, points), null, false);
    }
}
